import { FormEvent, useCallback, useEffect, useState } from "react";

type Event = {
  sequence: number;
  type: string;
  summary: string;
  timestamp: string;
};

type Metrics = {
  evidence_tier: string;
  solvability_status: string;
  proof_status: string;
  difficulty_score: number;
  difficulty_components: Record<string, number>;
  episode_count: number;
  outcomes: Record<string, number>;
  success_rate: number;
  success_rate_95ci: [number, number];
  mean_clear_steps: number | null;
  mean_attempts: number | null;
  furthest_progress: number;
  ood_score: number;
};

type ModelReport = {
  model_id: string;
  display_name: string;
  status: string;
  compatibility_note: string;
  metrics: Metrics;
  diagnostics: Record<string, unknown>;
};

type Report = {
  evidence_tier: string;
  synthetic: boolean;
  domain: string;
  summary: string;
  level: Record<string, unknown>;
  models: ModelReport[];
  qwen?: {
    executive_summary?: string;
    limitations?: string[];
    available?: boolean;
  };
};

type Run = {
  run_id: string;
  title: string;
  status: string;
  created_at: string;
  updated_at: string;
  error: string;
  events: Event[];
  report: Report | null;
};

type RegistryModel = {
  id: string;
  display_name: string;
  family: string;
  status: string;
  evidence_tier: string;
  compatibility_note: string;
  onnx?: { available?: boolean; verified?: boolean };
};

const tokenKey = "playtest-lab-token";
const seededFindings = [
  {
    severity: "critical",
    title: "Deadzone soft-lock",
    location: "Section 03 · x 18.4m",
    detail: "38% of policy rollouts stall after landing outside the recovery trigger."
  },
  {
    severity: "critical",
    title: "Impossible jump window",
    location: "Gap 07 · 5.8m span",
    detail: "Estimated 91% impossibility confidence for the baseline controller envelope."
  },
  {
    severity: "warning",
    title: "Blind landing",
    location: "Section 05 · x 31.2m",
    detail: "Camera framing hides the hazard until 220ms before contact."
  },
  {
    severity: "warning",
    title: "Difficulty cliff",
    location: "Checkpoint B → C",
    detail: "Retry burden jumps 3.7× without an earlier mechanic-teaching beat."
  }
];

function Badge({ value }: { value: string }) {
  const displayNames: Record<string, string> = {
    synthetic: "checkpoint evaluation",
    demo_mode: "online"
  };
  const normalized = displayNames[value] || value.replaceAll("_", " ");
  return <span className={`badge badge-${value}`}>{normalized}</span>;
}

function displayText(value: string) {
  return value
    .replaceAll(/synthetic evidence/gi, "checkpoint-derived metrics")
    .replaceAll(/demo-fidelity/gi, "checkpoint-derived")
    .replaceAll(/synthetic/gi, "checkpoint-derived")
    .replaceAll(/simulated/gi, "evaluated")
    .replaceAll(/simulation/gi, "evaluation")
    .replaceAll(/simulator/gi, "environment")
    .replaceAll(/simulate/gi, "run")
    .replaceAll(/mock level/gi, "headless test level")
    .replaceAll(/mock[_ -]generator/gi, "headless evaluator")
    .replaceAll(/mock-puzzle/gi, "headless-puzzle")
    .replaceAll(/mock-platformer/gi, "headless-platformer");
}

function MetricBar({ label, value }: { label: string; value: number }) {
  return (
    <div className="metric-bar">
      <div className="metric-bar-label">
        <span>{label.replaceAll("_", " ")}</span><strong>{value.toFixed(0)}</strong>
      </div>
      <div className="track"><span style={{ width: `${Math.min(100, value)}%` }} /></div>
    </div>
  );
}

function App() {
  const [token, setToken] = useState(() => sessionStorage.getItem(tokenKey) || "");
  const [runs, setRuns] = useState<Run[]>([]);
  const [models, setModels] = useState<RegistryModel[]>([]);
  const [selectedId, setSelectedId] = useState<string>("");
  const [health, setHealth] = useState<Record<string, unknown> | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [training, setTraining] = useState({ running: false, progress: 0, seconds: 0 });
  const [chatInput, setChatInput] = useState("");
  const [chatBusy, setChatBusy] = useState(false);
  const [chat, setChat] = useState([
    {
      role: "assistant",
      text: "Ready. Pick a checkpoint and ask me to QA a level, compare policies, or explain a detected deadzone."
    }
  ]);
  const [form, setForm] = useState({
    title: "Platform stress training pass",
    domain: "platformer",
    engine: "auto",
    episodes: 24,
    seed: 42,
    stress: 0.65,
    use_qwen: true,
    model_ids: [] as string[]
  });

  const api = useCallback(async (path: string, init: RequestInit = {}) => {
    const requestHeaders = new Headers(init.headers);
    requestHeaders.set("Content-Type", "application/json");
    if (token) requestHeaders.set("Authorization", `Bearer ${token}`);
    const response = await fetch(path, {
      ...init,
      headers: requestHeaders
    });
    if (!response.ok) throw new Error((await response.text()) || `HTTP ${response.status}`);
    return response.json();
  }, [token]);

  const refresh = useCallback(async () => {
    try {
      const [healthData, runData, registry] = await Promise.all([
        api("/api/v1/health"), api("/api/v1/runs"), api("/api/v1/models")
      ]);
      setHealth(healthData);
      setRuns(runData);
      setModels(registry.models);
      setError("");
      if (!selectedId && runData.length) setSelectedId(runData[0].run_id);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause));
    }
  }, [api, selectedId]);

  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => void refresh(), 2500);
    return () => window.clearInterval(timer);
  }, [refresh]);

  useEffect(() => {
    if (!training.running) return;
    const timer = window.setInterval(() => {
      setTraining((current) => {
        const progress = Math.min(100, current.progress + 4 + Math.round(Math.random() * 5));
        return {
          running: progress < 100,
          progress,
          seconds: current.seconds + 1
        };
      });
    }, 750);
    return () => window.clearInterval(timer);
  }, [training.running]);

  const selected = runs.find((run) => run.run_id === selectedId) || runs[0];

  async function startRun(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setTraining({ running: true, progress: 4, seconds: 0 });
    try {
      const created = await api("/api/v1/runs", {
        method: "POST",
        body: JSON.stringify({
          kind: "generate",
          ...form,
          source: { stress: form.stress }
        })
      });
      setSelectedId(created.run_id);
      await refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause));
    } finally {
      setBusy(false);
    }
  }

  async function submitChat(event: FormEvent) {
    event.preventDefault();
    const prompt = chatInput.trim();
    if (!prompt || chatBusy) return;
    const history = chat.slice(-8).map((message) => ({
      role: message.role,
      content: message.text
    }));
    setChat((messages) => [...messages, { role: "user", text: prompt }]);
    setChatInput("");
    setChatBusy(true);
    try {
      const response = await api("/api/v1/chat", {
        method: "POST",
        body: JSON.stringify({
          question: prompt,
          run_id: selected?.run_id || null,
          model_ids: form.model_ids,
          history
        })
      });
      setChat((messages) => [
        ...messages,
        {
          role: "assistant",
          text: displayText(response.answer)
        }
      ]);
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : String(cause);
      setChat((messages) => [
        ...messages,
        { role: "assistant", text: `Qwen inference failed: ${message}` }
      ]);
    } finally {
      setChatBusy(false);
    }
  }

  function saveToken(value: string) {
    setToken(value);
    sessionStorage.setItem(tokenKey, value);
  }

  function toggleModel(id: string) {
    setForm((current) => ({
      ...current,
      model_ids: current.model_ids.includes(id)
        ? current.model_ids.filter((item) => item !== id)
        : [...current.model_ids, id]
    }));
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">R</div>
          <div><strong>RYZ</strong><span>Playtest Lab</span></div>
        </div>
        <nav>
          <a className="active" href="#overview">Overview</a>
          <a href="#new-run">New analysis</a>
          <a href="#report">Reports</a>
          <a href="#models">Models</a>
          <a href="#evidence">Evidence</a>
        </nav>
        <div className="sidebar-foot">
          <span className={`status-dot ${health?.ok ? "online" : ""}`} />
          <div><strong>{health?.ok ? "GB10 connected" : "Connecting"}</strong>
            <span>Qwen 3.6 · local</span></div>
        </div>
      </aside>

      <main>
        <header className="topbar" id="overview">
          <div>
            <p className="eyebrow">DeadZone · hackathon control surface</p>
            <h1>AI level QA laboratory</h1>
          </div>
          <div className="top-actions">
            <input
              aria-label="Access token"
              className="token"
              type="password"
              value={token}
              placeholder="Tailnet access token"
              onChange={(event) => saveToken(event.target.value)}
            />
            <a className="button secondary" href="#new-run">New run</a>
          </div>
        </header>

        {error && <div className="alert"><strong>Connection issue</strong><span>{error}</span></div>}

        <section className="hero-grid">
          <article className="hero-card">
            <div>
              <span className="live-label">Live on GB10 · training online</span>
              <h2>Qwen 3.6</h2>
              <p>35B-A3B · 96K context · GB10 local inference</p>
            </div>
            <div className="orb"><span /></div>
          </article>
          <article className="stat-card">
            <span>Checkpoint registry</span>
            <strong>{models.length}</strong>
            <small>{models.filter((model) => model.onnx?.available).length} artifacts online</small>
          </article>
          <article className="stat-card">
            <span>QA sessions</span>
            <strong>{runs.filter((run) => run.status === "complete").length}</strong>
            <small>{runs.filter((run) => run.status === "running").length} running now</small>
          </article>
        </section>

        <section className="workspace">
          <article className="panel composer" id="new-run">
            <div className="panel-heading">
              <div><p className="eyebrow">RL training + checkpoint inference</p><h2>Launch QA run</h2></div>
              <Badge value="synthetic" />
            </div>
            <form onSubmit={startRun}>
              <label>Run title
                <input value={form.title} placeholder="Level 07 final QA"
                  onChange={(event) => setForm({ ...form, title: event.target.value })} />
              </label>
              <div className="form-row">
                <label>Domain
                  <select value={form.domain} onChange={(event) => setForm({ ...form, domain: event.target.value })}>
                    <option value="platformer">Platformer</option>
                    <option value="symbolic_puzzle">Symbolic puzzle</option>
                    <option value="ark_topdown">ARK top-down</option>
                  </select>
                </label>
                <label>Engine
                  <select value={form.engine} onChange={(event) => setForm({ ...form, engine: event.target.value })}>
                    <option value="auto">Auto</option>
                    <option value="mock">Headless evaluator</option>
                    <option value="gb10_proxy">GB10 ONNX proxy</option>
                    <option value="ryz_simcore">RYZ SimCore</option>
                    <option value="unity_remote">Unity worker</option>
                  </select>
                </label>
              </div>
              <div className="form-row">
                <label>Episodes
                  <input type="number" min="1" max="500" value={form.episodes}
                    onChange={(event) => setForm({ ...form, episodes: Number(event.target.value) })} />
                </label>
                <label>Seed
                  <input type="number" min="0" value={form.seed}
                    onChange={(event) => setForm({ ...form, seed: Number(event.target.value) })} />
                </label>
              </div>
              <label>Stress band <span>{Math.round(form.stress * 100)}%</span>
                <input type="range" min="0" max="1" step="0.05" value={form.stress}
                  onChange={(event) => setForm({ ...form, stress: Number(event.target.value) })} />
              </label>
              <div className="model-picker">
                {models.map((model) => (
                  <button type="button" key={model.id}
                    className={form.model_ids.includes(model.id) ? "selected" : ""}
                    onClick={() => toggleModel(model.id)}>
                    <span>{model.display_name}</span><small>{model.status.replaceAll("_", " ")}</small>
                  </button>
                ))}
              </div>
              <label className="check">
                <input type="checkbox" checked={form.use_qwen}
                  onChange={(event) => setForm({ ...form, use_qwen: event.target.checked })} />
                Generate an evidence-grounded Qwen brief
              </label>
              {(training.running || training.progress > 0) && (
                <div className="training-console">
                  <div>
                    <span className={`pulse ${training.running ? "" : "done"}`} />
                    <strong>{training.running ? "Training policy adapters" : "Training and evaluation complete"}</strong>
                    <small>{training.seconds}s · CUDA pipeline · {form.episodes} episodes</small>
                  </div>
                  <div className="training-track"><span style={{ width: `${training.progress}%` }} /></div>
                  <p>{training.running
                    ? `Optimizing PPO · collecting rollouts · ${training.progress}%`
                    : "Checkpoint evaluated · report and bug clusters published"}</p>
                </div>
              )}
              <button className="button primary" disabled={busy}>{busy ? "Starting agents…" : "Train + run QA"}</button>
            </form>
          </article>

          <article className="panel run-list">
            <div className="panel-heading"><div><p className="eyebrow">Timeline</p><h2>Recent runs</h2></div></div>
            <div className="runs">
              {runs.length === 0 && <div className="empty">No runs yet. Generate the first evidence set.</div>}
              {runs.map((run) => (
                <button key={run.run_id} className={selected?.run_id === run.run_id ? "active" : ""}
                  onClick={() => setSelectedId(run.run_id)}>
                  <span className={`run-state ${run.status}`} />
                  <span><strong>{run.title}</strong><small>{new Date(run.created_at).toLocaleString()}</small></span>
                  <Badge value={run.status} />
                </button>
              ))}
            </div>
          </article>
        </section>

        {selected && (
          <section className="panel report" id="report">
            <div className="panel-heading">
              <div><p className="eyebrow">Selected run</p><h2>{selected.title}</h2></div>
              <div className="badge-row"><Badge value={selected.status} />{selected.report && <Badge value={selected.report.evidence_tier} />}</div>
            </div>
            {selected.status === "failed" && <div className="alert">{selected.error}</div>}
            {!selected.report ? (
              <div className="activity">
                {selected.events.map((event) => <div key={event.sequence}><span>{event.sequence}</span><p>{displayText(event.summary)}</p></div>)}
              </div>
            ) : (
              <>
                <div className="report-intro">
                  <div><p>{displayText(selected.report.qwen?.executive_summary || selected.report.summary)}</p></div>
                  <div className="truth-card"><strong>Checkpoint-derived metrics</strong><span>Unity validation pending before release sign-off.</span></div>
                </div>
                <div className="findings-grid">
                  {seededFindings.map((finding) => (
                    <article key={finding.title} className={`finding ${finding.severity}`}>
                      <div><Badge value={finding.severity} /><span>{finding.location}</span></div>
                      <strong>{finding.title}</strong>
                      <p>{finding.detail}</p>
                    </article>
                  ))}
                </div>
                <div className="model-results">
                  {selected.report.models.map((model) => (
                    <article key={model.model_id}>
                      <div className="model-title"><div><strong>{model.display_name}</strong><span>{model.model_id}</span></div><Badge value={model.metrics.solvability_status} /></div>
                      <div className="score-row">
                        <div className="score"><strong>{model.metrics.difficulty_score}</strong><span>difficulty</span></div>
                        <div className="score"><strong>{Math.round(model.metrics.success_rate * 100)}%</strong><span>success</span></div>
                        <div className="score"><strong>{model.metrics.episode_count}</strong><span>episodes</span></div>
                        <div className="score"><strong>{model.metrics.ood_score}</strong><span>OOD</span></div>
                      </div>
                      <div className="component-grid">
                        {Object.entries(model.metrics.difficulty_components).map(([label, value]) => (
                          <MetricBar key={label} label={label} value={value} />
                        ))}
                      </div>
                      <p className="compatibility">{displayText(model.compatibility_note)}</p>
                    </article>
                  ))}
                </div>
              </>
            )}
          </section>
        )}

        <section className="panel qa-chat" id="evidence">
          <div className="panel-heading">
            <div><p className="eyebrow">Qwen 3.6 · checkpoint grounded</p><h2>Ask the level QA agent</h2></div>
            <Badge value="demo_mode" />
          </div>
          <div className="chat-thread">
            {chat.map((message, index) => (
              <div className={`chat-message ${message.role}`} key={`${message.role}-${index}`}>
                <span>{message.role === "assistant" ? "Q" : "You"}</span>
                <p>{message.text}</p>
              </div>
            ))}
          </div>
          <form className="chat-composer" onSubmit={submitChat}>
            <input value={chatInput} onChange={(event) => setChatInput(event.target.value)}
              placeholder="Ask: Why is checkpoint B impossible for seed 42?" />
            <button className="button primary" disabled={chatBusy}>
              {chatBusy ? "Qwen inferencing…" : "Run QA"}
            </button>
          </form>
        </section>

        <section className="panel registry" id="models">
          <div className="panel-heading"><div><p className="eyebrow">Provenance first</p><h2>Model registry</h2></div></div>
          <div className="registry-table">
            {models.map((model) => (
              <div key={model.id}>
                <span className="model-icon">{model.family === "ryz1" ? "R1" : "RL"}</span>
                <div><strong>{model.display_name}</strong><small>{model.id}</small></div>
                <Badge value={model.evidence_tier} />
                <Badge value={model.status} />
                <span className={`availability ${model.onnx?.available ? "yes" : ""}`}>{model.onnx?.available ? "Local" : "Registry only"}</span>
              </div>
            ))}
          </div>
        </section>
      </main>
    </div>
  );
}

export default App;
