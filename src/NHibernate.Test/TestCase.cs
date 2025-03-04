using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using log4net;
using NHibernate.Cfg;
using NHibernate.Connection;
using NHibernate.Engine;
using NHibernate.Mapping;
using NHibernate.Tool.hbm2ddl;
using NHibernate.Type;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using System.Text;
using NHibernate.Dialect;
using NHibernate.Driver;
using NHibernate.Engine.Query;
using NHibernate.SqlTypes;
using NHibernate.Util;
using NSubstitute;

namespace NHibernate.Test
{
	public abstract class TestCase
	{
		private const bool OutputDdl = false;
		protected Configuration cfg;
		private DebugSessionFactory _sessionFactory;
		private SchemaExport _schemaExport;

		private static readonly ILog log = LogManager.GetLogger(typeof(TestCase));
		private static readonly FieldInfo PlanCacheField;
		private Dialect.Dialect _dialect;
		private TestDialect _testDialect;

		static TestCase()
		{
			PlanCacheField = typeof(QueryPlanCache)
				                 .GetField("planCache", BindingFlags.NonPublic | BindingFlags.Instance)
			                 ?? throw new InvalidOperationException(
				                 "planCache field does not exist in QueryPlanCache.");
		}

		protected Dialect.Dialect Dialect
		{
			get { return _dialect ?? (_dialect = NHibernate.Dialect.Dialect.GetDialect(cfg.Properties)); }
		}

		protected TestDialect TestDialect
		{
			get { return _testDialect ?? (_testDialect = TestDialect.GetTestDialect(Dialect)); }
		}

		/// <summary>
		/// Mapping files used in the TestCase
		/// </summary>
		protected abstract string[] Mappings { get; }

		/// <summary>
		/// Assembly to load mapping files from (default is NHibernate.DomainModel).
		/// </summary>
		protected virtual string MappingsAssembly
		{
			get { return "NHibernate.DomainModel"; }
		}

		protected SchemaExport SchemaExport => _schemaExport ?? (_schemaExport = new SchemaExport(cfg));

		/// <summary>
		/// Creates the tables used in this TestCase
		/// </summary>
		[OneTimeSetUp]
		public void TestFixtureSetUp()
		{
			try
			{
				Configure();
				if (!AppliesTo(Dialect))
				{
					Assert.Ignore(GetType() + " does not apply to " + Dialect);
				}

				_sessionFactory = BuildSessionFactory();
				if (!AppliesTo(_sessionFactory))
				{
					Assert.Ignore(GetType() + " does not apply with the current session-factory configuration");
				}
				CreateSchema();
			}
			catch (Exception e)
			{
				Cleanup();
				log.Error("Error while setting up the test fixture", e);
				throw;
			}
		}

		protected void RebuildSessionFactory()
		{
			Sfi?.Close();
			_sessionFactory = BuildSessionFactory();
		}

		/// <summary>
		/// Removes the tables used in this TestCase.
		/// </summary>
		/// <remarks>
		/// If the tables are not cleaned up sometimes SchemaExport runs into
		/// Sql errors because it can't drop tables because of the FKs.  This 
		/// will occur if the TestCase does not have the same hbm.xml files
		/// included as a previous one.
		/// </remarks>
		[OneTimeTearDown]
		public void TestFixtureTearDown()
		{
			// If TestFixtureSetup fails due to an IgnoreException, it will still run the teardown.
			// We don't want to try to clean up again since the setup would have already done so.
			// If cfg is null already, that indicates it's already been cleaned up and we needn't.
			if (cfg != null)
			{
				if (!AppliesTo(Dialect))
					return;

				if (AppliesTo(_sessionFactory))
					DropSchema();
				Cleanup();
			}
		}

		protected virtual void OnSetUp()
		{
		}

		/// <summary>
		/// Set up the test. This method is not overridable, but it calls
		/// <see cref="OnSetUp" /> which is.
		/// </summary>
		[SetUp]
		public void SetUp()
		{
			OnSetUp();
		}

		protected virtual void OnTearDown()
		{
		}

		/// <summary>
		/// Checks that the test case cleans up after itself. This method
		/// is not overridable, but it calls <see cref="OnTearDown" /> which is.
		/// </summary>
		[TearDown]
		public void TearDown()
		{
			var testResult = TestContext.CurrentContext.Result;
			var fail = false;
			var testOwnTearDownDone = false;
			string badCleanupMessage = null;
			try
			{
				try
				{
					OnTearDown();
					testOwnTearDownDone = true;
				}
				finally
				{
					try
					{
						var wereClosed = _sessionFactory.CheckSessionsWereClosed();
						var wasCleaned = CheckDatabaseWasCleaned();
						var wereConnectionsClosed = CheckConnectionsWereClosed();
						fail = !wereClosed || !wasCleaned || !wereConnectionsClosed;

						if (fail)
						{
							badCleanupMessage = "Test didn't clean up after itself. session closed: " + wereClosed + "; database cleaned: " +
												wasCleaned
												+ "; connection closed: " + wereConnectionsClosed;
							if (testResult != null && testResult.Outcome.Status == TestStatus.Failed)
							{
								// Avoid hiding a test failure (asserts are usually not hidden, but other exception would be).
								badCleanupMessage = GetCombinedFailureMessage(testResult, badCleanupMessage, null);
							}
						}
					}
					catch (Exception ex)
					{
						if (testOwnTearDownDone)
							throw;

						// Do not hide the test own teardown failure.
						log.Error("TearDown cleanup failure, while test own teardown has failed. Logging cleanup failure", ex);
					}
				}
			}
			catch (Exception ex)
			{
				if (testResult == null || testResult.Outcome.Status != TestStatus.Failed)
					throw;

				// Avoid hiding a test failure (asserts are usually not hidden, but other exceptions would be).
				var exType = ex.GetType();
				Assert.Fail(GetCombinedFailureMessage(testResult,
					exType.Namespace + "." + exType.Name + " " + ex.Message,
					ex.StackTrace));
			}

			if (fail)
			{
				Assert.Fail(badCleanupMessage);
			}
		}

		private string GetCombinedFailureMessage(TestContext.ResultAdapter result, string tearDownFailure, string tearDownStackTrace)
		{
			var message = new StringBuilder()
				.Append("The test failed and then failed to cleanup. Test failure is: ")
				.AppendLine(result.Message)
				.Append("Tear-down failure is: ")
				.AppendLine(tearDownFailure)
				.AppendLine("Test failure stack trace is: ")
				.AppendLine(result.StackTrace);

			if (!string.IsNullOrEmpty(tearDownStackTrace))
				message.AppendLine("Tear-down failure stack trace is:")
					.Append(tearDownStackTrace);

			return message.ToString();
		}

		protected virtual bool CheckDatabaseWasCleaned()
		{
			var allClassMetadata = Sfi.GetAllClassMetadata();
			if (allClassMetadata.Count == 0)
			{
				// Return early in the case of no mappings, also avoiding
				// a warning when executing the HQL below.
				return true;
			}

			var explicitPolymorphismEntities = allClassMetadata.Values.Where(x => x is NHibernate.Persister.Entity.IQueryable queryable && queryable.IsExplicitPolymorphism).ToArray();

			//TODO: Maybe add explicit load query checks 
			if (explicitPolymorphismEntities.Length == allClassMetadata.Count)
				return true;

			bool empty;
			using (ISession s = OpenSession())
			{
				IList objects = s.CreateQuery("from System.Object o").List();
				empty = objects.Count == 0;
			}

			if (!empty)
			{
				log.Error("Test case didn't clean up the database after itself, re-creating the schema");
				DropSchema();
				CreateSchema();
			}

			return empty;
		}

		private bool CheckConnectionsWereClosed()
		{
			if (_sessionFactory?.DebugConnectionProvider?.HasOpenConnections != true)
			{
				return true;
			}

			log.Error("Test case didn't close all open connections, closing");
			_sessionFactory.DebugConnectionProvider.CloseAllConnections();
			return false;
		}

		/// <summary>
		/// (Re)Create the configuration.
		/// </summary>
		protected void Configure()
		{
			cfg = TestConfigurationHelper.GetDefaultConfiguration();

			AddMappings(cfg);

			Configure(cfg);

			ApplyCacheSettings(cfg);
		}

		protected virtual void AddMappings(Configuration configuration)
		{
			Assembly assembly = Assembly.Load(MappingsAssembly);

			foreach (string file in Mappings)
			{
				configuration.AddResource(MappingsAssembly + "." + file, assembly);
			}
		}

		protected virtual void CreateSchema()
		{
			using (var optionalConnection = OpenConnectionForSchemaExport())
				SchemaExport.Create(OutputDdl, true, optionalConnection);
		}

		protected virtual void DropSchema()
		{
			DropSchema(OutputDdl, SchemaExport, Sfi, OpenConnectionForSchemaExport);
		}

		public static void DropSchema(bool useStdOut, SchemaExport export, ISessionFactoryImplementor sfi, Func<DbConnection> getConnection = null)
		{
			if (sfi?.ConnectionProvider.Driver is FirebirdClientDriver fbDriver)
			{
				// Firebird will pool each connection created during the test and will marked as used any table
				// referenced by queries. It will at best delays those tables drop until connections are actually
				// closed, or immediately fail dropping them.
				// This results in other tests failing when they try to create tables with same name.
				// By clearing the connection pool the tables will get dropped. This is done by the following code.
				// Moved from NH1908 test case, contributed by Amro El-Fakharany.
				fbDriver.ClearPool(null);
			}

			using(var optionalConnection = getConnection?.Invoke())
				export.Drop(useStdOut, true, optionalConnection);
		}

		/// <summary>
		/// Specific connection is required only for Database multi-tenancy. In other cases can be null 
		/// </summary>
		protected virtual DbConnection OpenConnectionForSchemaExport()
		{
			return null;
		}

		protected virtual DebugSessionFactory BuildSessionFactory()
		{
			return new DebugSessionFactory(cfg.BuildSessionFactory());
		}

		private void Cleanup()
		{
			Sfi?.Close();
			_sessionFactory = null;
			cfg = null;
			_schemaExport = null;
		}

		public int ExecuteStatement(string sql)
		{
			if (cfg == null)
			{
				cfg = TestConfigurationHelper.GetDefaultConfiguration();
			}

			using (IConnectionProvider prov = ConnectionProviderFactory.NewConnectionProvider(cfg.Properties))
			{
				var conn = prov.GetConnection();

				try
				{
					using (var tran = conn.BeginTransaction())
					using (var comm = conn.CreateCommand())
					{
						comm.CommandText = sql;
						comm.Transaction = tran;
						comm.CommandType = CommandType.Text;
						int result = comm.ExecuteNonQuery();
						tran.Commit();
						return result;
					}
				}
				finally
				{
					prov.CloseConnection(conn);
				}
			}
		}

		public int ExecuteStatement(ISession session, ITransaction transaction, string sql)
		{
			using (var cmd = session.Connection.CreateCommand())
			{
				cmd.CommandText = sql;
				if (transaction != null)
					transaction.Enlist(cmd);
				return cmd.ExecuteNonQuery();
			}
		}

		protected ISessionFactoryImplementor Sfi => _sessionFactory;

		protected virtual ISession OpenSession()
		{
			return Sfi.OpenSession();
		}

		protected virtual ISession OpenSession(IInterceptor sessionLocalInterceptor)
		{
			return Sfi.WithOptions().Interceptor(sessionLocalInterceptor).OpenSession();
		}

		protected virtual void ApplyCacheSettings(Configuration configuration)
		{
			if (CacheConcurrencyStrategy == null)
			{
				return;
			}

			foreach (PersistentClass clazz in configuration.ClassMappings)
			{
				bool hasLob = false;
				foreach (Property prop in clazz.PropertyClosureIterator)
				{
					if (prop.Value.IsSimpleValue)
					{
						IType type = ((SimpleValue)prop.Value).Type;
						if (ReferenceEquals(type, NHibernateUtil.BinaryBlob))
						{
							hasLob = true;
						}
					}
				}
				if (!hasLob && !clazz.IsInherited)
				{
					configuration.SetCacheConcurrencyStrategy(clazz.EntityName, CacheConcurrencyStrategy);
				}
			}

			foreach (Mapping.Collection coll in configuration.CollectionMappings)
			{
				configuration.SetCollectionCacheConcurrencyStrategy(coll.Role, CacheConcurrencyStrategy);
			}
		}

		#region Properties overridable by subclasses

		protected virtual bool AppliesTo(Dialect.Dialect dialect)
		{
			return true;
		}

		protected virtual bool AppliesTo(ISessionFactoryImplementor factory)
		{
			return true;
		}

		protected virtual void Configure(Configuration configuration)
		{
		}

		protected virtual string CacheConcurrencyStrategy
		{
			get { return "nonstrict-read-write"; }
			//get { return null; }
		}

		#endregion

		#region Utilities

		protected DateTime RoundForDialect(DateTime value)
		{
			return AbstractDateTimeType.Round(value, Dialect.TimestampResolutionInTicks);
		}

		private static readonly Dictionary<string, HashSet<System.Type>> DialectsNotSupportingStandardFunction =
			new Dictionary<string, HashSet<System.Type>>
			{
				{"bit_length", new HashSet<System.Type> {typeof (SQLiteDialect)}},
				{"extract", new HashSet<System.Type> {typeof (SQLiteDialect)}},
				{
					"nullif",
					new HashSet<System.Type>
					{
						// Actually not supported by the db engine. (Well, could likely still be done with a case when override.)
						typeof (MsSqlCeDialect),
						typeof (MsSqlCe40Dialect)
					}}
			};

		protected bool IsFunctionSupported(string functionName)
		{
			// We could test Sfi.SQLFunctionRegistry.HasFunction(functionName) which has the advantage of
			// accounting for additional functions added in configuration. But Dialect is normally never
			// null, while Sfi could be not yet initialized, depending from where this function is called.
			// Furthermore there are currently no additional functions added in configuration for NHibernate
			// tests.
			var dialect = Dialect;
			if (!dialect.Functions.ContainsKey(functionName))
				return false;

			return !DialectsNotSupportingStandardFunction.TryGetValue(functionName, out var dialects) ||
				!dialects.Contains(dialect.GetType());
		}

		protected void AssumeFunctionSupported(string functionName)
		{
			// We could test Sfi.SQLFunctionRegistry.HasFunction(functionName) which has the advantage of
			// accounting for additional functions added in configuration. But Dialect is normally never
			// null, while Sfi could be not yet initialized, depending from where this function is called.
			// Furthermore there are currently no additional functions added in configuration for NHibernate
			// tests.
			var dialect = Dialect;
			Assume.That(
				dialect.Functions,
				Does.ContainKey(functionName),
				$"{dialect} doesn't support {functionName} function.");

			if (!DialectsNotSupportingStandardFunction.TryGetValue(functionName, out var dialects))
				return;
			Assume.That(
				dialects,
				Does.Not.Contain(dialect.GetType()),
				$"{dialect} doesn't support {functionName} standard function.");
		}

		protected SoftLimitMRUCache GetQueryPlanCache()
		{
			return (SoftLimitMRUCache) PlanCacheField.GetValue(Sfi.QueryPlanCache);
		}

		protected void ClearQueryPlanCache()
		{
			GetQueryPlanCache().Clear();
		}

		protected Substitute<Dialect.Dialect> SubstituteDialect()
		{
			var origDialect = Sfi.Settings.Dialect;
			var dialectProperty = (PropertyInfo) ReflectHelper.GetProperty<Settings, Dialect.Dialect>(o => o.Dialect);
			var forPartsOfMethod = ReflectHelper.GetMethodDefinition(() => Substitute.ForPartsOf<object>());
			var substitute = (Dialect.Dialect) forPartsOfMethod.MakeGenericMethod(origDialect.GetType())
																.Invoke(null, new object[] { new object[0] });
			substitute.GetCastTypeName(Arg.Any<SqlType>())
			          .ReturnsForAnyArgs(x => origDialect.GetCastTypeName(x.ArgAt<SqlType>(0)));

			dialectProperty.SetValue(Sfi.Settings, substitute);

			return new Substitute<Dialect.Dialect>(substitute, Dispose);

			void Dispose()
			{
				dialectProperty.SetValue(Sfi.Settings, origDialect);
			}
		}

		protected static int GetTotalOccurrences(string content, string substring)
		{
			if (string.IsNullOrEmpty(substring))
			{
				throw new ArgumentNullException(nameof(substring));
			}

			int occurrences = 0, index = 0;
			while ((index = content.IndexOf(substring, index)) >= 0)
			{
				occurrences++;
				index += substring.Length;
			}

			return occurrences;
		}

		protected struct Substitute<TType> : IDisposable
		{
			private readonly System.Action _disposeAction;

			public Substitute(TType value, System.Action disposeAction)
			{
				Value = value;
				_disposeAction = disposeAction;
			}

			public TType Value { get; }

			public void Dispose()
			{
				_disposeAction();
			}
		}

		#endregion
	}
}
