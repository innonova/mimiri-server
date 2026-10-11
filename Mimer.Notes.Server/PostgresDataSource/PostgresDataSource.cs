using Mimer.Framework;
using Npgsql;

namespace Mimer.Notes.Server {
	public partial class PostgresDataSource {
		private string _connectionString;
		private NpgsqlDataSource _postgres;
		private byte[] _userEncryptionKey;

		public PostgresDataSource(string connectionString, byte[] userEncryptionKey) {
			_connectionString = connectionString;
			_postgres = NpgsqlDataSource.Create(connectionString);
			_userEncryptionKey = userEncryptionKey;
		}


		public void TearDown(bool keepLogs) { }

		public void CreateDatabase() {
			Dev.Log("CreateDatabase");
			try {
				CreateCommonFunctions();
				CreateUserTables();
				// after the user tables: admin_users references mimer_user
				CreateRoleTables();
				CreateKeyTables();
				CreateNoteTables();
				CreateShareTables();
				CreateStatsTables();
				CreateCommentTables();
			}
			catch (Exception ex) {
				Dev.Log(ex);
			}
		}

		private void CreateCommonFunctions() {
			using var command = _postgres.CreateCommand();
			command.CommandText = """
			CREATE OR REPLACE FUNCTION update_modified_column()
			RETURNS TRIGGER AS $$
			BEGIN
			    NEW.modified = current_timestamp;
			    RETURN NEW;
			END;
			$$ language 'plpgsql';

			CREATE OR REPLACE FUNCTION update_sync_column()
			RETURNS TRIGGER AS $$
			BEGIN
			    NEW.sync = nextval('sync_sequence');
			    RETURN NEW;
			END;
			$$ language 'plpgsql';

			CREATE SEQUENCE IF NOT EXISTS sync_sequence
					START WITH 1
					INCREMENT BY 1
					NO MINVALUE
					NO MAXVALUE
					CACHE 1;
			""";
			command.ExecuteNonQuery();
		}

	}
}
