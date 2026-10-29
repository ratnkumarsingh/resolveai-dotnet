import { createHash, randomUUID, timingSafeEqual } from "node:crypto";
import { existsSync } from "node:fs";
import fastifyStatic from "@fastify/static";
import Fastify, { type FastifyReply, type FastifyRequest } from "fastify";
import { CreateRcaBody, FeedbackBody, PatchRcaBody, StartInvestigationBody, type AppConfig, type ReviewState } from "@resolveai/shared";
import type { z } from "zod";
import type { Config } from "./config.js";
import { type Db, get, run } from "./db/index.js";
import { seedDemoData } from "./db/seed.js";
import { LlmError, type LlmProvider } from "./llm/provider.js";
import { loadEvidence } from "./pipeline/evidence.js";
import { findIncident, listIncidents, listServices } from "./services/incidents.js";
import {
  getInvestigation, getInvestigationRow, latestInvestigationId, listInvestigationRuns, liveRunsToday, startInvestigation,
} from "./services/investigations.js";
import { createRca, getRca, listRcas, patchRca, rcaMarkdown } from "./services/rca.js";
import type { Systems } from "./services/investigations.js";

export interface Providers {
  recorded: LlmProvider;
  /** Present when an API key is configured. */
  live?: LlmProvider;
}

export interface AppDeps {
  db: Db;
  providers: Providers;
  /** MCP systems checked during investigations; absent when MCP is turned off. */
  systems?: Systems;
  config: Pick<Config, "demoMode" | "model" | "llmMode" | "liveAccessCode" | "liveDailyLimit"> & {
    /** Allow demo reset from any client IP (the Docker port is published on 127.0.0.1 only). */
    demoResetAnyIp?: boolean;
    webDist?: string;
  };
}

class HttpError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
  }
}

function parse<S extends z.ZodType>(schema: S, body: unknown): z.infer<S> {
  const r = schema.safeParse(body ?? {});
  if (!r.success) throw new HttpError(400, `Invalid request: ${r.error.issues.map((i) => `${i.path.join(".") || "body"} ${i.message}`).join("; ")}`);
  return r.data;
}

const LOCAL = new Set(["127.0.0.1", "::1", "::ffff:127.0.0.1"]);
const REVIEW_STATES = new Set<ReviewState>(["none", "pending", "verified"]);

const digest = (s: string) => createHash("sha256").update(s).digest();

export function buildApp(deps: AppDeps) {
  const { db, providers, config, systems } = deps;
  const app = Fastify({ logger: false });
  const defaultProvider = config.llmMode === "live" && providers.live ? providers.live : providers.recorded;
  const liveAvailable = !!providers.live && (config.llmMode === "live" || !!config.liveAccessCode);

  const hasAccessCode = (req: FastifyRequest) => {
    const sent = req.headers["x-access-code"];
    if (!config.liveAccessCode || typeof sent !== "string" || !sent) return false;
    return timingSafeEqual(digest(sent), digest(config.liveAccessCode));
  };

  /**
   * Picks the provider for one model call. Live costs money, so unless the server runs live by default
   * it needs the access code, and every live call counts against the daily limit.
   */
  const providerFor = (req: FastifyRequest, mode: "live" | "recorded"): LlmProvider => {
    if (mode === "recorded") return providers.recorded;
    if (!providers.live) throw new HttpError(400, "Live mode is not configured on this server (no API key).");
    if (config.llmMode !== "live") {
      if (!config.liveAccessCode) throw new HttpError(400, "Live mode is not enabled on this server (no access code configured).");
      if (!hasAccessCode(req)) throw new HttpError(403, "Live mode needs a valid access code. Enter it under Settings.");
    }
    if (liveRunsToday(db) >= config.liveDailyLimit) {
      throw new HttpError(429, `Today's live limit (${config.liveDailyLimit} model calls) is used up. Use recorded mode or try again tomorrow (UTC).`);
    }
    return providers.live;
  };

  app.setErrorHandler((err: unknown, _req, reply) => {
    if (err instanceof HttpError) return reply.status(err.status).send({ error: err.message });
    if (err instanceof LlmError) return reply.status(502).send({ error: err.message });
    const status = (err as { statusCode?: number }).statusCode;
    if (status && status < 500) return reply.status(status).send({ error: (err as Error).message });
    console.error(err);
    return reply.status(500).send({ error: "Internal server error" });
  });

  const incidentOr404 = (idOrKey: string) => {
    const incident = findIncident(db, idOrKey);
    if (!incident) throw new HttpError(404, "Incident not found");
    return incident;
  };

  app.get("/api/config", async (): Promise<AppConfig> => ({
    llm_mode: defaultProvider.mode,
    model: defaultProvider.mode === "live" ? config.model : "recorded fixtures",
    live_available: liveAvailable,
    live_model: providers.live ? config.model : null,
    live_daily_limit: config.liveDailyLimit,
    live_runs_today: liveRunsToday(db),
    demo_mode: config.demoMode,
    data_source: "Demo dataset",
    max_follow_up_calls: systems?.maxFollowUps ?? 0,
  }));

  app.get("/api/systems", async () => ({ systems: systems?.hub.status() ?? [], enabled: !!systems }));

  app.get("/api/incidents", async (req: FastifyRequest<{ Querystring: Record<string, string | undefined> }>) => {
    const q = req.query;
    return {
      ...listIncidents(db, {
        status: q.status || undefined,
        severity: q.severity || undefined,
        service: q.service || undefined,
        q: q.q?.trim() || undefined,
        review: REVIEW_STATES.has(q.review as ReviewState) ? (q.review as ReviewState) : undefined,
        page: Math.max(1, Number(q.page) || 1),
        pageSize: Math.min(100, Math.max(1, Number(q.page_size) || 25)),
      }),
      services: listServices(db),
    };
  });

  app.get("/api/incidents/:id", async (req: FastifyRequest<{ Params: { id: string } }>) => {
    const incident = incidentOr404(req.params.id);
    return {
      incident,
      latest_investigation_id: latestInvestigationId(db, incident.id) ?? null,
      investigations: listInvestigationRuns(db, incident.id),
      rca_drafts: listRcas(db, incident.id),
    };
  });

  app.get("/api/incidents/:id/evidence", async (req: FastifyRequest<{ Params: { id: string } }>) => {
    return loadEvidence(db, incidentOr404(req.params.id));
  });

  app.post("/api/incidents/:id/investigations", async (req: FastifyRequest<{ Params: { id: string } }>, reply: FastifyReply) => {
    const incident = incidentOr404(req.params.id);
    const body = parse(StartInvestigationBody, req.body);
    const running = get<{ id: string }>(db,
      "SELECT id FROM investigations WHERE incident_id = ? AND status IN ('queued', 'running') LIMIT 1", incident.id);
    if (running) return reply.status(202).send({ investigation_id: running.id, status: "running" });
    const llm = providerFor(req, body.mode ?? defaultProvider.mode);
    const id = startInvestigation({ db, llm, systems }, incident, body.question?.trim() || null);
    return reply.status(202).send({ investigation_id: id, status: "queued" });
  });

  app.get("/api/investigations/:id", async (req: FastifyRequest<{ Params: { id: string } }>) => {
    const inv = getInvestigation(db, req.params.id);
    if (!inv) throw new HttpError(404, "Investigation not found");
    return inv;
  });

  app.post("/api/investigations/:id/feedback", async (req: FastifyRequest<{ Params: { id: string } }>, reply: FastifyReply) => {
    const row = getInvestigationRow(db, req.params.id);
    if (!row) throw new HttpError(404, "Investigation not found");
    const body = parse(FeedbackBody, req.body);
    if (body.hypothesis_id) {
      const owned = get(db, "SELECT 1 FROM hypotheses WHERE id = ? AND investigation_id = ?", body.hypothesis_id, row.id);
      if (!owned) throw new HttpError(400, "Hypothesis does not belong to this investigation");
    }
    if (body.helpful === undefined && !body.comment?.trim() && !body.verified_root_cause?.trim()) {
      throw new HttpError(400, "Feedback needs a helpful flag, a comment or a verified root cause");
    }
    const id = randomUUID();
    run(db,
      `INSERT INTO feedback (id, investigation_id, hypothesis_id, helpful, comment, verified_root_cause, created_at)
       VALUES (?, ?, ?, ?, ?, ?, ?)`,
      id, row.id, body.hypothesis_id ?? null, body.helpful === undefined ? null : body.helpful ? 1 : 0,
      body.comment?.trim() || null, body.verified_root_cause?.trim() || null, new Date().toISOString());
    return reply.status(201).send({ id });
  });

  app.post("/api/incidents/:id/rca-drafts", async (req: FastifyRequest<{ Params: { id: string } }>, reply: FastifyReply) => {
    const incident = incidentOr404(req.params.id);
    const body = parse(CreateRcaBody, req.body);
    const inv = getInvestigationRow(db, body.investigation_id);
    if (!inv || inv.incident_id !== incident.id) throw new HttpError(400, "That investigation does not belong to this incident");
    if (inv.status !== "completed") throw new HttpError(409, "Run an investigation to completion before drafting an RCA");
    // The draft is written in the same mode as the investigation it summarises.
    const llm = providerFor(req, inv.llm_mode);
    const rca = await createRca({ db, llm }, incident, body.investigation_id);
    return reply.status(201).send({ rca_draft_id: rca.id, rca });
  });

  app.get("/api/rca-drafts/:id", async (req: FastifyRequest<{ Params: { id: string } }>) => {
    const rca = getRca(db, req.params.id);
    if (!rca) throw new HttpError(404, "RCA draft not found");
    return rca;
  });

  app.patch("/api/rca-drafts/:id", async (req: FastifyRequest<{ Params: { id: string } }>) => {
    const rca = patchRca(db, req.params.id, parse(PatchRcaBody, req.body));
    if (!rca) throw new HttpError(404, "RCA draft not found");
    return rca;
  });

  app.get("/api/rca-drafts/:id/export.md", async (req: FastifyRequest<{ Params: { id: string } }>, reply: FastifyReply) => {
    const rca = getRca(db, req.params.id);
    if (!rca) throw new HttpError(404, "RCA draft not found");
    const incident = incidentOr404(rca.incident_id);
    return reply
      .header("content-type", "text/markdown; charset=utf-8")
      .header("content-disposition", `attachment; filename="${incident.key}-rca-v${rca.version}.md"`)
      .send(rcaMarkdown(rca, incident));
  });

  app.get("/api/knowledge/:id", async (req: FastifyRequest<{ Params: { id: string } }>) => {
    const doc = get(db, "SELECT id, title, doc_type, service_name, body FROM knowledge_documents WHERE id = ?", req.params.id);
    if (!doc) throw new HttpError(404, "Document not found");
    return doc;
  });

  app.post("/api/demo/reset", async (req: FastifyRequest) => {
    if (!config.demoMode) throw new HttpError(404, "Not found");
    if (!config.demoResetAnyIp && !LOCAL.has(req.ip) && !hasAccessCode(req)) {
      throw new HttpError(403, "Demo reset is only available from localhost or with the access code");
    }
    const running = get(db, "SELECT 1 FROM investigations WHERE status IN ('queued', 'running') LIMIT 1");
    if (running) throw new HttpError(409, "An investigation is still running; wait for it to finish before resetting");
    seedDemoData(db);
    return { ok: true };
  });

  // In the Docker image the API also serves the built web app. Unknown page paths get index.html for client
  // routing; missing files (anything with an extension) stay 404 so a broken asset link fails visibly.
  if (config.webDist && existsSync(config.webDist)) {
    app.register(fastifyStatic, { root: config.webDist, wildcard: false });
    app.setNotFoundHandler((req, reply) => {
      const path = req.url.split("?")[0];
      if (req.method === "GET" && !path.startsWith("/api/") && !/\.\w+$/.test(path)) return reply.sendFile("index.html");
      return reply.status(404).send({ error: "Not found" });
    });
  }

  return app;
}
