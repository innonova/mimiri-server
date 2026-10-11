using System;
using System.Security.Cryptography;
using System.Text;
using Mimer.Framework;
using Mimer.Framework.Json;
using Mimer.Notes.Model.DataTypes;
using Npgsql;

namespace Mimer.Notes.Server {
	public partial class PostgresDataSource {

		// Database creation methods
		private void CreateUserTables() {
			using var command = _postgres.CreateCommand();
			command.CommandText = """
				CREATE TABLE IF NOT EXISTS public."user_type" (
				  id bigint NOT NULL PRIMARY KEY,
				  name character varying(50) NOT NULL,
				  max_total_bytes bigint NOT NULL,
				  max_note_bytes bigint NOT NULL,
				  max_note_count bigint NOT NULL,
				  max_history_entries bigint NOT NULL,
				  CONSTRAINT "user_type_name" UNIQUE (name)
				);

				-- the plans; a database that has them keeps its own
				INSERT INTO public."user_type" (id, name, max_total_bytes, max_note_bytes, max_note_count, max_history_entries) VALUES
				  (1, 'free', 104857600, 1048576, 2000, 10),
				  (2, 'unlimited', 10737418240, 10737418240, 1000000, 100000),
				  (4, 'tier 1', 1048576000, 10485760, 10000, 10),
				  (5, 'tier 2', 2097152000, 10485760, 20000, 10),
				  (6, 'tier 3', 5242880000, 10485760, 50000, 10)
				ON CONFLICT DO NOTHING;

				CREATE TABLE IF NOT EXISTS public."mimer_user" (
				  id uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
				  username character varying(50) NOT NULL,
				  username_upper character varying(50) COLLATE pg_catalog."default" GENERATED ALWAYS AS (upper((username)::text)) STORED,
				  user_type bigint NOT NULL DEFAULT 1,
				  data text NOT NULL,
				  server_config text NOT NULL DEFAULT '{}',
				  client_config text NOT NULL DEFAULT '{}',
				  created timestamp without time zone NOT NULL DEFAULT current_timestamp,
				  modified timestamp without time zone NOT NULL DEFAULT current_timestamp,
				  CONSTRAINT "mimer_user_username" UNIQUE (username),
				  CONSTRAINT "mimer_user_username_upper" UNIQUE (username_upper)
				);

				DO
				$$BEGIN
				CREATE TRIGGER update_mimer_user_modified BEFORE UPDATE ON public."mimer_user" FOR EACH ROW EXECUTE PROCEDURE update_modified_column();
				EXCEPTION
				   WHEN duplicate_object THEN
				      NULL;
				END;$$;

				CREATE TABLE IF NOT EXISTS public."user_stats" (
				  user_id uuid NOT NULL PRIMARY KEY,
				  created timestamp without time zone NOT NULL DEFAULT current_timestamp,
				  last_activity timestamp without time zone NOT NULL DEFAULT current_timestamp,
				  size bigint NOT NULL DEFAULT 0,
				  logins bigint NOT NULL DEFAULT 0,
				  reads bigint NOT NULL DEFAULT 0,
				  writes bigint NOT NULL DEFAULT 0,
				  read_bytes bigint NOT NULL DEFAULT 0,
				  write_bytes bigint NOT NULL DEFAULT 0,
				  creates bigint NOT NULL DEFAULT 0,
				  deletes bigint NOT NULL DEFAULT 0,
				  notifications bigint NOT NULL DEFAULT 0,
				  notes bigint NOT NULL DEFAULT 0
				);

				-- update_user_stats_size() and GetUser use it; tables made before it was added lack it
				ALTER TABLE public."user_stats" ADD COLUMN IF NOT EXISTS notes bigint NOT NULL DEFAULT 0;

				CREATE OR REPLACE FUNCTION update_user_stats_size()
				RETURNS TRIGGER AS $$
				BEGIN
				 	IF (TG_OP = 'DELETE') THEN
						UPDATE user_stats SET size = size - OLD.size, notes = notes - OLD.note_count WHERE user_id = OLD.user_id;
					ELSIF (TG_OP = 'UPDATE') THEN
						IF (NEW.size <> OLD.size OR NEW.note_count <> OLD.note_count) THEN
							UPDATE user_stats SET size = size + NEW.size - OLD.size, notes = notes + NEW.note_count - OLD.note_count WHERE user_id = NEW.user_id;
						END IF;
					ELSIF (TG_OP = 'INSERT') THEN
						UPDATE user_stats SET size = size + NEW.size WHERE user_id = NEW.user_id;
					END IF;
				    RETURN NULL;
				END;
				$$ language 'plpgsql';
				""";
			command.ExecuteNonQuery();
		}

		// User-related methods
		private byte[] ReadToEnd(Stream stream) {
			using (MemoryStream OStream = new MemoryStream()) {
				byte[] OBuffer = new byte[25600];
				while (true) {
					int ORead = stream.Read(OBuffer, 0, OBuffer.Length);
					if (ORead == 0) {
						return OStream.ToArray();
					}
					OStream.Write(OBuffer, 0, ORead);
				}
			}
		}

		private string EncryptUser(MimerUser user) {
			var aes = Aes.Create();
			aes.KeySize = 256;
			aes.Key = _userEncryptionKey;
			aes.GenerateIV();
			byte[] data = Encoding.UTF8.GetBytes(user.ToJsonString());
			JsonObject json = new JsonObject();
			json.String("iv", Convert.ToBase64String(aes.IV));

			using (ICryptoTransform transform = aes.CreateEncryptor()) {
				using (MemoryStream stream = new MemoryStream()) {
					using (CryptoStream cryptoStream = new CryptoStream(stream, transform, CryptoStreamMode.Write)) {
						cryptoStream.Write(data, 0, data.Length);
					}
					json.String("data", Convert.ToBase64String(stream.ToArray()));
				}
			}
			return json.ToString();
		}

		private MimerUser DecryptUser(string data, Guid stableId, long size, long noteCount, long typeId, string serverConfig, string clientConfig) {
			var json = new JsonObject(data);
			if (json.Has("publicKey")) {
				return new MimerUser(json, stableId, size, noteCount, typeId, serverConfig, clientConfig);
			}
			var aes = Aes.Create();
			aes.KeySize = 256;
			aes.Key = _userEncryptionKey;
			aes.IV = Convert.FromBase64String(json.String("iv"));
			var cipherText = Convert.FromBase64String(json.String("data"));
			using (ICryptoTransform transform = aes.CreateDecryptor()) {
				using (MemoryStream stream = new MemoryStream(cipherText)) {
					using (CryptoStream cryptoStream = new CryptoStream(stream, transform, CryptoStreamMode.Read)) {
						return new MimerUser(new JsonObject(Encoding.UTF8.GetString(ReadToEnd(cryptoStream))), stableId, size, noteCount, typeId, serverConfig, clientConfig);
					}
				}
			}
		}

		public List<UserType> GetUserTypes() {
			try {
				var result = new List<UserType>();
				using var command = _postgres.CreateCommand();
				command.CommandText = "SELECT id, name, max_total_bytes, max_note_bytes, max_note_count, max_history_entries FROM user_type";
				var reader = command.ExecuteReader();
				while (reader.Read()) {
					result.Add(new UserType(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
				}
				return result;
			}
			catch (Exception ex) {
				Dev.Log(ex);
				throw;
			}
		}

		public async Task<bool> CreateUser(MimerUser user) {
			try {
				await using var connection = await _postgres.OpenConnectionAsync();
				await using var transaction = await connection.BeginTransactionAsync();

				try {
					await using var command = new NpgsqlCommand("", connection, transaction);
					command.CommandText = @"INSERT INTO mimer_user (username, data) VALUES (@username, @data)";
					command.Parameters.AddWithValue("@username", user.Username);
					command.Parameters.AddWithValue("@data", EncryptUser(user));
					await command.ExecuteNonQueryAsync();

					command.CommandText = @"INSERT INTO user_stats (user_id) VALUES ((SELECT id FROM mimer_user WHERE username_upper = upper(@username)))";
					await command.ExecuteNonQueryAsync();
					await transaction.CommitAsync();
					return true;
				}
				catch {
					await transaction.RollbackAsync();
					throw;
				}
			}
			catch (Exception ex) {
				Dev.Log(_connectionString, ex);
				return false;
			}
		}

		public async Task<bool> UpdateUser(string oldUsername, MimerUser user) {
			try {
				using var command = _postgres.CreateCommand();
				if (oldUsername != user.Username) {
					command.CommandText = @"UPDATE mimer_user SET username = @username, data = @data WHERE username = @old_username";
				}
				else {
					command.CommandText = @"UPDATE mimer_user SET data = @data WHERE username = @old_username";
				}
				command.Parameters.AddWithValue("@username", user.Username);
				command.Parameters.AddWithValue("@data", EncryptUser(user));
				command.Parameters.AddWithValue("@old_username", oldUsername);
				await command.ExecuteNonQueryAsync();
				return true;
			}
			catch (Exception ex) {
				Dev.Log(_connectionString, ex);
				return false;
			}
		}

		public async Task<MimerUser?> GetUser(string username) {
			try {
				using var command = _postgres.CreateCommand();
				command.CommandText = @"SELECT u.data, u.id, s.size, s.notes, u.user_type, u.server_config, u.client_config FROM mimer_user as u INNER JOIN user_stats as s ON s.user_id = u.id WHERE u.username_upper = upper(@username)";
				command.Parameters.AddWithValue("@username", username);
				using var reader = await command.ExecuteReaderAsync();
				if (await reader.ReadAsync()) {
					return DecryptUser(reader.GetString(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6));
				}
			}
			catch (Exception ex) {
				Dev.Log(_connectionString, ex);
			}
			return null;
		}

		public async Task<bool> DeleteUser(Guid userId) {
			// be aware that test cleanup is happening in payment service
			try {
				await using var connection = await _postgres.OpenConnectionAsync();
				await using var transaction = await connection.BeginTransactionAsync();
				int count = 0;
				try {
					await using var command = new NpgsqlCommand("", connection, transaction);

					command.CommandText = @"select k.key_name from mimer_key as k WHERE user_id = @user_id AND (SELECT count(*) FROM mimer_key as k2 WHERE k2.key_name = k.key_name) < 2";
					command.Parameters.AddWithValue("@user_id", userId);
					var keyNames = new List<Guid>();
					using (var reader = await command.ExecuteReaderAsync()) {
						while (await reader.ReadAsync()) {
							keyNames.Add(reader.GetGuid(0));
						}
					}
					foreach (var keyName in keyNames) {
						command.Parameters.Clear();
						command.Parameters.AddWithValue("@key_name", keyName);

						command.CommandText = @"DELETE FROM mimer_note_item WHERE note_id in (SELECT note.id FROM mimer_note as note WHERE key_name = @key_name)";
						count = await command.ExecuteNonQueryAsync();

						command.CommandText = @"DELETE FROM mimer_note as note WHERE key_name = @key_name";
						count = await command.ExecuteNonQueryAsync();

						command.CommandText = @"DELETE FROM deleted_mimer_note WHERE key_name = @key_name";
						count = await command.ExecuteNonQueryAsync();
					}

					command.CommandText = @"DELETE FROM mimer_key WHERE user_id = @user_id";
					command.Parameters.Clear();
					command.Parameters.AddWithValue("@user_id", userId);
					count = await command.ExecuteNonQueryAsync();

					command.CommandText = @"DELETE FROM user_stats WHERE user_id = @user_id";
					count = await command.ExecuteNonQueryAsync();

					command.CommandText = @"DELETE FROM mimer_user WHERE id = @user_id";
					count = await command.ExecuteNonQueryAsync();

					await transaction.CommitAsync();
					return true;
				}
				catch {
					await transaction.RollbackAsync();
					throw;
				}
			}
			catch (Exception ex) {
				Dev.Log(_connectionString, ex);
			}
			return false;
		}
	}
}
