import { describe, expect, it } from "vitest";
import { LlmError } from "../src/llm/provider.js";
import { makeApp, output, runInvestigation, ScriptedProvider } from "./helpers.js";

describe("incidents", () => {
  it("lists seeded incidents with filters and returns detail and evidence", async () => {
    const { app } = makeApp();
    const list = (await app.inject("/api/incidents")).json();
    expect(list.total).toBe(6);
    expect(list.items[0].key).toBe("INC-1046");

    const sev1 = (await app.inject("/api/incidents?severity=SEV-1")).json();
    expect(sev1.items.map((i: { key: string }) => i.key).sort()).toEqual(["INC-1042", "INC-1046"]);

    const detail = (await app.inject("/api/incidents/INC-1042")).json();
    expect(detail.incident.title).toMatch(/Payment API/);

    const evidence = (await app.inject("/api/incidents/INC-1042/evidence")).json();
    expect(evidence.logs.length).toBeGreaterThan(5);
    expect(evidence.deployments.some((d: { version: string }) => d.version === "v2.8.1")).toBe(true);
    expect(evidence.error_signals[0].signature).toBe("db_conn_acquire_timeout");

    expect((await app.inject("/api/incidents/INC-9999")).statusCode).toBe(404);
  });
});

describe("investigation", () => {
  it("happy path: ranked hypotheses with valid evidence and the similar incident", async () => {
    const { app } = makeApp();
    const inv = await runInvestigation(app, "INC-1042");
    expect(inv.status).toBe("completed");
    expect(inv.hypotheses.length).toBeGreaterThanOrEqual(2);
    expect(inv.hypotheses[0].confidence).toBe("high");
    expect(inv.hypotheses[0].title).toMatch(/pool/i);
    for (const h of inv.hypotheses) {
      expect(h.evidence.length).toBeGreaterThan(0);
      for (const e of h.evidence) expect(e.excerpt).not.toBe("(record not in packet)");
    }
    expect(inv.similar_incidents[0].historical.key).toBe("INC-0881");
    expect(inv.similar_incidents[0].similarity_reason).toMatch(/connection/i);
    expect(inv.validation_issues.some((i: string) => /Redacted/.test(i))).toBe(true);
  });

  it("reports insufficient evidence instead of inventing a cause", async () => {
    const { app } = makeApp();
    const inv = await runInvestigation(app, "INC-1043");
    expect(inv.status).toBe("completed");
    expect(inv.insufficient_evidence).toBe(true);
    expect(inv.hypotheses).toHaveLength(0);
  });

  it("removes citations that are not in the evidence packet", async () => {
    const llm = new ScriptedProvider((packet) =>
      output({
        hypotheses: [
          {
            title: "Real", explanation: "x", confidence: "high", evidence_ids: [packet.items.find((i) => i.type === "log")!.id, "log:does-not-exist"],
            counter_evidence: [], unknowns: [], recommended_checks: [],
          },
          {
            title: "Made up", explanation: "x", confidence: "high", evidence_ids: ["dep:fabricated"],
            counter_evidence: [], unknowns: [], recommended_checks: [],
          },
        ],
        similar_incidents: [{ incident_id: "hist:not-a-candidate", similarity_reason: "x" }],
      }),
    );
    const { app } = makeApp(llm);
    const inv = await runInvestigation(app, "INC-1042");
    const real = inv.hypotheses.find((h: { title: string }) => h.title === "Real");
    const fake = inv.hypotheses.find((h: { title: string }) => h.title === "Made up");
    expect(real.evidence.map((e: { ref: string }) => e.ref)).not.toContain("log:does-not-exist");
    expect(real.evidence).toHaveLength(1);
    expect(fake.speculative).toBe(true);
    expect(fake.confidence).toBe("low");
    expect(inv.hypotheses[0].title).toBe("Real");
    expect(inv.validation_issues.join("\n")).toMatch(/log:does-not-exist/);
    expect(inv.validation_issues.join("\n")).toMatch(/not among retrieved candidates/);
  });

  it("treats prompt-injection text in logs as data and strips destructive checks", async () => {
    const { app } = makeApp();
    const inv = await runInvestigation(app, "INC-1044");
    expect(inv.status).toBe("completed");
    // The injected line may appear as cited evidence, but the analysis itself must not adopt it.
    const text = JSON.stringify(inv.hypotheses.map((h: { title: string; explanation: string; recommended_checks: unknown }) =>
      [h.title, h.explanation, h.recommended_checks]));
    expect(text).not.toMatch(/DNS outage/i);
    expect(text).not.toMatch(/rm -rf/);

    const llm = new ScriptedProvider((packet) =>
      output({
        hypotheses: [{
          title: "Queue corrupted", explanation: "x", confidence: "medium", evidence_ids: [packet.items[0].id],
          counter_evidence: [], unknowns: [],
          recommended_checks: [
            { step: "Run rm -rf /var/lib/queue to clear it", why: "x", expected_signal: "x" },
            { step: "Check the queue depth dashboard", why: "x", expected_signal: "x" },
          ],
        }],
      }),
    );
    const scripted = makeApp(llm);
    const inv2 = await runInvestigation(scripted.app, "INC-1044");
    expect(inv2.hypotheses[0].recommended_checks.map((c: { step: string }) => c.step)).toEqual(["Check the queue depth dashboard"]);
    expect(inv2.validation_issues.join("\n")).toMatch(/destructive/);
    // The model never saw the raw password or email.
    const sent = JSON.stringify(llm.packets[0]);
    expect(sent).not.toContain("hunter2");
    expect(sent).not.toContain("jane.doe@example.com");
  });

  it("retries once on malformed output", async () => {
    const llm = new ScriptedProvider((packet, call) => {
      if (call === 1) throw new LlmError("bad json", true);
      return output({
        hypotheses: [{ title: "ok", explanation: "x", confidence: "medium", evidence_ids: [packet.items[0].id], counter_evidence: [], unknowns: [], recommended_checks: [] }],
      });
    });
    const { app } = makeApp(llm);
    const inv = await runInvestigation(app, "INC-1042");
    expect(inv.status).toBe("completed");
    expect(llm.packets).toHaveLength(2);
    expect(inv.validation_issues.join("\n")).toMatch(/retried once/);
  });

  it("fails gracefully on provider errors and leaves the incident intact", async () => {
    const llm = new ScriptedProvider(() => {
      throw new LlmError("The Claude API is rate limiting requests. Wait a minute and run the investigation again.");
    });
    const { app } = makeApp(llm);
    const inv = await runInvestigation(app, "INC-1042");
    expect(inv.status).toBe("failed");
    expect(inv.error_message).toMatch(/rate limiting/);
    expect((await app.inject("/api/incidents/INC-1042")).statusCode).toBe(200);
  });
});

describe("feedback and RCA", () => {
  it("records feedback, drafts an RCA, persists edits and exports Markdown", async () => {
    const { app } = makeApp();
    const inv = await runInvestigation(app, "INC-1042");
    const top = inv.hypotheses[0];

    const fb = await app.inject({
      method: "POST", url: `/api/investigations/${inv.id}/feedback`,
      payload: { hypothesis_id: top.id, helpful: true, verified_root_cause: "pool.max set to 20 in db-client.yaml by v2.8.1" },
    });
    expect(fb.statusCode).toBe(201);
    const after = (await app.inject(`/api/investigations/${inv.id}`)).json();
    expect(after.hypotheses[0].feedback).toEqual({ helpful: true });

    const created = await app.inject({ method: "POST", url: "/api/incidents/INC-1042/rca-drafts", payload: { investigation_id: inv.id } });
    expect(created.statusCode).toBe(201);
    const rca = created.json().rca;
    expect(rca.version).toBe(1);
    expect(rca.root_cause).toMatch(/^Verified by engineer: pool.max/);
    expect(rca.timeline.length).toBeGreaterThan(2);
    expect(rca.evidence_ids.length).toBeGreaterThan(0);

    const patched = await app.inject({ method: "PATCH", url: `/api/rca-drafts/${rca.id}`, payload: { impact: "Edited impact", status: "reviewed" } });
    expect(patched.statusCode).toBe(200);
    const reloaded = (await app.inject(`/api/rca-drafts/${rca.id}`)).json();
    expect(reloaded.impact).toBe("Edited impact");
    expect(reloaded.status).toBe("reviewed");

    // Regenerating creates version 2 and keeps the edited version 1.
    const v2 = (await app.inject({ method: "POST", url: "/api/incidents/INC-1042/rca-drafts", payload: { investigation_id: inv.id } })).json().rca;
    expect(v2.version).toBe(2);
    expect((await app.inject(`/api/rca-drafts/${rca.id}`)).json().impact).toBe("Edited impact");

    const md = await app.inject(`/api/rca-drafts/${rca.id}/export.md`);
    expect(md.headers["content-type"]).toMatch(/markdown/);
    expect(md.body).toContain("AI-generated draft — review required");
    expect(md.body).toContain("Edited impact");
  });

  it("rejects an RCA for an investigation that has not completed or belongs elsewhere", async () => {
    const { app } = makeApp();
    const inv = await runInvestigation(app, "INC-1042");
    const res = await app.inject({ method: "POST", url: "/api/incidents/INC-1044/rca-drafts", payload: { investigation_id: inv.id } });
    expect(res.statusCode).toBe(400);
    expect(res.json().error).toMatch(/does not belong/);
  });
});

describe("demo reset", () => {
  it("restores the dataset from localhost and refuses remote callers", async () => {
    const { app } = makeApp();
    await runInvestigation(app, "INC-1042");
    const remote = await app.inject({ method: "POST", url: "/api/demo/reset", remoteAddress: "10.1.2.3" });
    expect(remote.statusCode).toBe(403);

    const ok = await app.inject({ method: "POST", url: "/api/demo/reset" });
    expect(ok.statusCode).toBe(200);
    expect((await app.inject("/api/incidents/INC-1042")).json().latest_investigation_id).toBeNull();

    // A second run after reset gives the same result (recorded mode is deterministic).
    const a = await runInvestigation(app, "INC-1042");
    await app.inject({ method: "POST", url: "/api/demo/reset" });
    const b = await runInvestigation(app, "INC-1042");
    const shape = (inv: { hypotheses: { title: string; evidence: { ref: string }[] }[] }) =>
      inv.hypotheses.map((h) => [h.title, h.evidence.map((e) => e.ref)]);
    expect(shape(b)).toEqual(shape(a));
  });
});
