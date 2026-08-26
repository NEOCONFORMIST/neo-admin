# NEO ADMIN SQLite storage

The production server plugin stores its administrator data in:

```text
game/csgo/addons/cs2fixes/configs/neo_admin.sqlite3
```

Set `VOICEBRIDGE_DATABASE_FILE` before CS2 starts to use another absolute path.
The plugin logs the active path at startup.

The database uses WAL mode and contains these tables:

- `neo_meta`: database schema metadata.
- `neo_documents`: versioned JSON documents named `accounts`, `game_admins`,
  `audit`, `bans`, `discipline`, and `operations`.

The Windows application continues to access these records through NEO ADMIN's
authenticated protocol. Raw SQL is intentionally not exposed through RCON or a
MetaMod command because the accounts document contains administrator secrets.

SQLite's JSON functions make the documents queryable. For example:

```sql
SELECT name, schema_version, updated_utc FROM neo_documents ORDER BY name;
SELECT json_pretty(json) FROM neo_documents WHERE name = 'bans';
SELECT value FROM neo_meta WHERE key = 'schema_version';
```

On the first database-backed startup, each existing JSON file is imported in a
transaction and renamed with the suffix `.migrated-to-sqlite.bak`. These backup
files are not read again after a successful import.

The database and its WAL files should be treated as credentials. Keep them
private, do not place them under a web root, and take backups with the SQLite
backup API or while the CS2 service is stopped.
