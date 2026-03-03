using System;
using System.IO;

namespace CryptoNotes.Services
{
    public static class Constants
    {
        public const string DatabaseFilename = "CryptoNotesSQLite.db3";
        public const string EncryptedDatabaseFilename = "CryptoNotesEncrypted.db3";

        public const SQLite.SQLiteOpenFlags Flags =
            // open the database in read/write mode
            SQLite.SQLiteOpenFlags.ReadWrite |
            // create the database if it doesn't exist
            SQLite.SQLiteOpenFlags.Create |
            // enable multi-threaded database access
            SQLite.SQLiteOpenFlags.SharedCache;

        private static string BasePath =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        public static string DatabasePath =>
            Path.Combine(BasePath, DatabaseFilename);

        public static string EncryptedDatabasePath =>
            Path.Combine(BasePath, EncryptedDatabaseFilename);

        public static string MigrationCompletePath =>
            Path.Combine(BasePath, ".db_migration_complete");

        public static string DbSaltPath =>
            Path.Combine(BasePath, ".db_salt");
    }
}
