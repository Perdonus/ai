import { FormEvent, useEffect, useState } from "react";
import { convertFileSrc, invoke } from "@tauri-apps/api/core";

type ProviderKind =
  | "sosiski_bot"
  | "open_ai"
  | "open_router"
  | "gemini"
  | "mistral"
  | "hugging_face";

type ModelRoute = {
  provider: ProviderKind;
  base_url: string;
  api_key: string;
  model: string;
};

type AppConfig = {
  text_route: ModelRoute;
  use_separate_vision: boolean;
  vision_route: ModelRoute;
  use_separate_ocr: boolean;
  ocr_route: ModelRoute;
  weather_location: string;
  weather_units: "metric" | "imperial";
  max_steps: number;
  confirmation_policy: "auto" | "ask" | "block";
};

type ModelOption = {
  id: string;
  label: string;
};

type AgentStatus = {
  active_task: string | null;
  logs: string[];
};

type WeatherSnapshot = {
  location: string;
  temperature_c: number | null;
  wind_speed_kmh: number | null;
  description: string;
  updated_at: string;
  stale: boolean;
};

type InstalledWidget = {
  manifest: {
    id: string;
    name: string;
    description: string;
    entry_html: string;
    version: string;
  };
  folder: string;
  entry_file: string;
};

type ScreenCapture = {
  path: string;
  width: number;
  height: number;
};

const providerLabels: Record<ProviderKind, string> = {
  sosiski_bot: "SosiskiBot",
  open_ai: "OpenAI",
  open_router: "OpenRouter",
  gemini: "Gemini",
  mistral: "Mistral",
  hugging_face: "Hugging Face"
};

const providerDefaults: Record<ProviderKind, string> = {
  sosiski_bot: "https://sosiskibot.ru/v1",
  open_ai: "https://api.openai.com/v1",
  open_router: "https://openrouter.ai/api/v1",
  gemini: "https://generativelanguage.googleapis.com/v1beta",
  mistral: "https://api.mistral.ai/v1",
  hugging_face: "https://router.huggingface.co/v1"
};

const emptyRoute = (provider: ProviderKind): ModelRoute => ({
  provider,
  base_url: providerDefaults[provider],
  api_key: "",
  model: ""
});

const fallbackConfig: AppConfig = {
  text_route: { ...emptyRoute("sosiski_bot"), model: "gpt-4o-mini" },
  use_separate_vision: false,
  vision_route: emptyRoute("gemini"),
  use_separate_ocr: false,
  ocr_route: emptyRoute("mistral"),
  weather_location: "Moscow",
  weather_units: "metric",
  max_steps: 12,
  confirmation_policy: "auto"
};

export default function App() {
  const [config, setConfig] = useState<AppConfig>(fallbackConfig);
  const [prompt, setPrompt] = useState("");
  const [status, setStatus] = useState<AgentStatus>({ active_task: null, logs: [] });
  const [weather, setWeather] = useState<WeatherSnapshot | null>(null);
  const [widgets, setWidgets] = useState<InstalledWidget[]>([]);
  const [widgetName, setWidgetName] = useState("");
  const [capture, setCapture] = useState<ScreenCapture | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(true);
  const [textModels, setTextModels] = useState<ModelOption[]>([]);
  const [visionModels, setVisionModels] = useState<ModelOption[]>([]);
  const [ocrModels, setOcrModels] = useState<ModelOption[]>([]);

  useEffect(() => {
    void refreshAll();
    const id = window.setInterval(() => void refreshStatus(), 1500);
    return () => window.clearInterval(id);
  }, []);

  async function refreshAll() {
    await Promise.all([refreshConfig(), refreshStatus(), refreshWeather(), refreshWidgets()]);
  }

  async function refreshConfig() {
    setConfig(await invoke<AppConfig>("load_config"));
  }

  async function refreshStatus() {
    setStatus(await invoke<AgentStatus>("agent_status"));
  }

  async function refreshWeather() {
    setWeather(await invoke<WeatherSnapshot>("get_weather"));
  }

  async function refreshWidgets() {
    setWidgets(await invoke<InstalledWidget[]>("list_widgets"));
  }

  async function takeScreen() {
    setCapture(await invoke<ScreenCapture>("capture_screen"));
  }

  async function submitTask(event: FormEvent) {
    event.preventDefault();
    if (!prompt.trim()) return;
    await invoke("run_task", { prompt });
    setPrompt("");
    await refreshStatus();
  }

  async function saveConfig(event: FormEvent) {
    event.preventDefault();
    await invoke("save_config", { config });
    await refreshConfig();
  }

  async function installWidget(event: FormEvent) {
    event.preventDefault();
    if (!widgetName.trim()) return;
    await invoke("install_widget_from_github", { widget_name: widgetName });
    setWidgetName("");
    await refreshWidgets();
  }

  async function loadModels(kind: "text" | "vision" | "ocr") {
    const route =
      kind === "text" ? config.text_route : kind === "vision" ? config.vision_route : config.ocr_route;
    const models = await invoke<ModelOption[]>("list_models", { route });
    if (kind === "text") setTextModels(models);
    if (kind === "vision") setVisionModels(models);
    if (kind === "ocr") setOcrModels(models);
  }

  function updateRoute(kind: "text" | "vision" | "ocr", next: Partial<ModelRoute>) {
    const key = kind === "text" ? "text_route" : kind === "vision" ? "vision_route" : "ocr_route";
    const current = config[key];
    setConfig({ ...config, [key]: { ...current, ...next } });
  }

  function routeEditor(
    title: string,
    route: ModelRoute,
    models: ModelOption[],
    kind: "text" | "vision" | "ocr",
    enabled = true
  ) {
    return (
      <section className={`route-card ${enabled ? "" : "route-card--disabled"}`}>
        <div className="row">
          <div>
            <p className="eyebrow">{title}</p>
            <h3>{providerLabels[route.provider]}</h3>
          </div>
          <button className="ghost" type="button" onClick={() => void loadModels(kind)} disabled={!enabled}>
            Загрузить модели
          </button>
        </div>
        <label>
          Provider
          <select
            value={route.provider}
            disabled={!enabled}
            onChange={(event) => {
              const provider = event.target.value as ProviderKind;
              updateRoute(kind, { provider, base_url: providerDefaults[provider] });
            }}
          >
            {Object.entries(providerLabels).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </label>
        <label>
          Base URL
          <input
            value={route.base_url}
            disabled={!enabled}
            onChange={(event) => updateRoute(kind, { base_url: event.target.value })}
          />
        </label>
        <label>
          API key
          <input
            type="password"
            value={route.api_key}
            disabled={!enabled}
            onChange={(event) => updateRoute(kind, { api_key: event.target.value })}
          />
        </label>
        <label>
          Model
          <select
            value={route.model}
            disabled={!enabled}
            onChange={(event) => updateRoute(kind, { model: event.target.value })}
          >
            <option value="">Выбери модель</option>
            {models.map((model) => (
              <option key={model.id} value={model.id}>
                {model.label}
              </option>
            ))}
          </select>
        </label>
        <label>
          Или впиши модель вручную
          <input
            value={route.model}
            disabled={!enabled}
            onChange={(event) => updateRoute(kind, { model: event.target.value })}
          />
        </label>
      </section>
    );
  }

  return (
    <main className="shell">
      <section className="panel hero">
        <div>
          <p className="eyebrow">Desktop AI Agent</p>
          <h1>Мультипровайдерный агент с text, vision и OCR</h1>
          <p className="muted">
            Теперь можно отдельно выбрать текстовую, вижн и OCR-модель: хоть GPT через
            SosiskiBot, vision через Gemini, а OCR через Mistral.
          </p>
        </div>
        <button className="ghost" onClick={() => setSettingsOpen((value) => !value)}>
          {settingsOpen ? "Скрыть настройки" : "Показать настройки"}
        </button>
      </section>

      <section className="grid">
        <form className="panel prompt-card" onSubmit={submitTask}>
          <label htmlFor="prompt" className="eyebrow">
            Запрос агенту
          </label>
          <textarea
            id="prompt"
            value={prompt}
            onChange={(event) => setPrompt(event.target.value)}
            placeholder="Используй text через GPT 5.4, vision через Gemini, OCR через Mistral и решай задачу по шагам."
            rows={8}
          />
          <div className="row">
            <button type="submit" disabled={Boolean(status.active_task)}>
              {status.active_task ? "Агент работает" : "Запустить"}
            </button>
            <button className="ghost" type="button" onClick={() => void invoke("cancel_task")}>
              Стоп
            </button>
          </div>
          <div className="tool-grid">
            <span>Multi API</span>
            <span>Vision fallback</span>
            <span>OCR fallback</span>
            <span>Model lists</span>
            <span>Mouse drag</span>
            <span>Type text</span>
          </div>
        </form>

        <section className="panel weather-card">
          <div className="row">
            <div>
              <p className="eyebrow">Weather</p>
              <h2>{weather?.location ?? config.weather_location}</h2>
            </div>
            <button className="ghost" onClick={() => void refreshWeather()}>
              Обновить
            </button>
          </div>
          <strong className="temperature">
            {weather?.temperature_c == null ? "--" : `${weather.temperature_c}°C`}
          </strong>
          <p className="muted">{weather?.description ?? "Нет данных"}</p>
          <p className="caption">
            Ветер: {weather?.wind_speed_kmh == null ? "--" : `${weather.wind_speed_kmh} км/ч`}
          </p>
        </section>
      </section>

      <section className="grid">
        <section className="panel">
          <div className="row">
            <div>
              <p className="eyebrow">Screen</p>
              <h2>Ручной захват экрана</h2>
            </div>
            <button onClick={() => void takeScreen()}>Снять экран</button>
          </div>
          <p className="muted">
            {capture ? `${capture.width}x${capture.height} • ${capture.path}` : "Пока не снимали."}
          </p>
        </section>

        <section className="panel">
          <p className="eyebrow">Input</p>
          <h2>Low-level automation</h2>
          <p className="muted">
            Мышь и клавиатура поддерживают down, up, hold, drag, combos и type_text.
          </p>
        </section>
      </section>

      <section className="panel widget-installer">
        <div className="row">
          <div>
            <p className="eyebrow">Runtime Widgets</p>
            <h2>Установка из нашей widget-ветки</h2>
          </div>
          <button className="ghost" onClick={() => void refreshWidgets()}>
            Обновить список
          </button>
        </div>
        <form className="widget-form" onSubmit={installWidget}>
          <input
            value={widgetName}
            onChange={(event) => setWidgetName(event.target.value)}
            placeholder="widget folder name"
          />
          <button type="submit">Скачать виджет</button>
        </form>
        <div className="widget-grid">
          {widgets.map((widget) => (
            <article className="widget-card" key={widget.manifest.id}>
              <div className="widget-copy">
                <p className="eyebrow">{widget.manifest.version || "widget"}</p>
                <h3>{widget.manifest.name}</h3>
                <p className="muted">{widget.manifest.description}</p>
              </div>
              <iframe
                title={widget.manifest.name}
                src={convertFileSrc(widget.entry_file)}
                className="widget-frame"
              />
            </article>
          ))}
          {widgets.length === 0 ? <p className="muted">Виджеты пока не найдены.</p> : null}
        </div>
      </section>

      <section className="panel">
        <div className="row">
          <div>
            <p className="eyebrow">Лог задач</p>
            <h2>{status.active_task ? "Выполняется" : "Ожидание"}</h2>
          </div>
        </div>
        <div className="log-list">
          {status.logs.map((entry, index) => (
            <pre key={`${entry}-${index}`}>{entry}</pre>
          ))}
          {status.logs.length === 0 ? <p className="muted">Журнал пуст.</p> : null}
        </div>
      </section>

      {settingsOpen ? (
        <form className="panel settings" onSubmit={saveConfig}>
          <div className="row">
            <div>
              <p className="eyebrow">Настройки ИИ</p>
              <h2>Маршруты моделей и fallback-флажки</h2>
            </div>
            <button type="submit">Сохранить</button>
          </div>

          <div className="toggle-row">
            <label className="toggle">
              <input
                type="checkbox"
                checked={config.use_separate_vision}
                onChange={(event) =>
                  setConfig({ ...config, use_separate_vision: event.target.checked })
                }
              />
              <span>Вижн модель отдельно</span>
            </label>
            <label className="toggle">
              <input
                type="checkbox"
                checked={config.use_separate_ocr}
                onChange={(event) =>
                  setConfig({ ...config, use_separate_ocr: event.target.checked })
                }
              />
              <span>OCR модель отдельно</span>
            </label>
          </div>

          <div className="routes-grid">
            {routeEditor("Text Route", config.text_route, textModels, "text", true)}
            {routeEditor("Vision Route", config.vision_route, visionModels, "vision", config.use_separate_vision)}
            {routeEditor("OCR Route", config.ocr_route, ocrModels, "ocr", config.use_separate_ocr)}
          </div>

          <div className="settings-grid">
            <label>
              Weather location
              <input
                value={config.weather_location}
                onChange={(event) =>
                  setConfig({ ...config, weather_location: event.target.value })
                }
              />
            </label>
            <label>
              Max steps
              <input
                type="number"
                min={1}
                max={50}
                value={config.max_steps}
                onChange={(event) =>
                  setConfig({ ...config, max_steps: Number(event.target.value) || 1 })
                }
              />
            </label>
            <label>
              Confirmation
              <select
                value={config.confirmation_policy}
                onChange={(event) =>
                  setConfig({
                    ...config,
                    confirmation_policy: event.target.value as AppConfig["confirmation_policy"]
                  })
                }
              >
                <option value="auto">Auto</option>
                <option value="ask">Ask</option>
                <option value="block">Block</option>
              </select>
            </label>
            <label>
              Weather units
              <select
                value={config.weather_units}
                onChange={(event) =>
                  setConfig({
                    ...config,
                    weather_units: event.target.value as AppConfig["weather_units"]
                  })
                }
              >
                <option value="metric">Metric</option>
                <option value="imperial">Imperial</option>
              </select>
            </label>
          </div>
        </form>
      ) : null}
    </main>
  );
}
