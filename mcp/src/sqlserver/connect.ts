import type { McpConfig } from "../common/config.js";
import { ToolError } from "../common/result.js";

/** Runs one fixed statement with named parameters. Tests swap in a fake. */
export type Query = (sql: string, params?: Record<string, string | number | Date>) => Promise<Record<string, unknown>[]>;

/** Wraps a statement so it runs as the read-only login and always reverts, even when it fails. */
export function asLogin(login: string, text: string) {
  return `EXECUTE AS LOGIN = N'${login}';\nBEGIN TRY\n;${text.trim()};\nEND TRY\nBEGIN CATCH\n  REVERT;\n  THROW;\nEND CATCH;\nREVERT;`;
}

/**
 * Opens a lazily connected pool. Windows sign-in goes through msnodesqlv8 (ODBC); a SQL login goes through
 * tedious over TCP and reads MSSQL_USER / MSSQL_PASSWORD from the environment, never from the config file.
 * ApplicationIntent=ReadOnly is set too, as a hint; the reader login's permissions are what actually keep it read-only.
 */
export function connect(cfg: McpConfig["sqlserver"], env: NodeJS.ProcessEnv = process.env): Query {
  type Pool = { request(): { input(name: string, value: unknown): unknown; query(sql: string): Promise<{ recordset?: Record<string, unknown>[] }> } };
  let pool: Promise<Pool> | null = null;

  const open = async (): Promise<Pool> => {
    if (cfg.driver === "msnodesqlv8") {
      const sql = (await import("mssql/msnodesqlv8.js")).default;
      const driver = env.MSSQL_ODBC_DRIVER ?? "ODBC Driver 17 for SQL Server";
      return new sql.ConnectionPool({
        connectionString: `Driver={${driver}};Server=${cfg.server};Database=master;Trusted_Connection=yes;` +
          `TrustServerCertificate=${cfg.trust_server_certificate ? "yes" : "no"};ApplicationIntent=ReadOnly;`,
        pool: { max: 2 },
      } as never).connect() as unknown as Pool;
    }
    if (!env.MSSQL_USER || !env.MSSQL_PASSWORD) throw new ToolError("Set MSSQL_USER and MSSQL_PASSWORD in .env for the SQL Server reader login.");
    const sql = (await import("mssql")).default;
    return new sql.ConnectionPool({
      server: cfg.server, port: cfg.port, user: env.MSSQL_USER, password: env.MSSQL_PASSWORD, database: "master",
      options: { encrypt: true, trustServerCertificate: cfg.trust_server_certificate, readOnlyIntent: true },
      pool: { max: 2 }, connectionTimeout: 5000, requestTimeout: 8000,
    }).connect() as unknown as Pool;
  };

  return async (text, params = {}) => {
    pool ??= open().catch((err) => {
      pool = null;
      throw err instanceof ToolError ? err : new ToolError(`Could not connect to SQL Server ${cfg.server}: ${(err as Error).message}`);
    });
    const req = (await pool).request();
    for (const [k, v] of Object.entries(params)) req.input(k, v);
    return (await req.query(cfg.execute_as ? asLogin(cfg.execute_as, text) : text)).recordset ?? [];
  };
}
