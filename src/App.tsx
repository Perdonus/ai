import { FormEvent, useEffect, useState } from "react";
import { convertFileSrc, invoke } from "@tauri-apps/api/core";

type AppConfig = {
  base_url: string;
  api_key: string;
  text_model: string;
  vision_model: string;
  weather_location: string;
  weather_units: "metric" | "imperial";
  max_steps: number;
  confirmation_policy: "auto" | "ask" | "block";
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

const fallbackConfig: AppConfig = {
  base_url: "https://sosiskibot.ru/v1",
  api_key: "",
  text_model: "gpt-4o-mini",
  vision_model: "gpt-4o",
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
    await invoke("install_widget_from_github", {
      widget_name: widgetName
    });
    setWidgetName("");
    await refreshWidgets();
  }

  return (
    <main className="shell">
      <section className="panel hero">
        <div>
          <p className="eyebrow">Desktop AI Agent</p>
          <h1>Агент с глазами, руками и runtime-виджетами</h1>
          <p className="muted">
            Каждый шаг может смотреть экран, двигать мышь, зажимать кнопки, печатать,
            гонять shell и ставить виджеты из нашей widget-ветки без ребилда.
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
            placeholder="Открой браузер, зайди в нужный репозиторий, собери проект, если нужен виджет — найди его в widget-ветке и установи."
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
            <span>Screen Vision</span>
            <span>Mouse Down/Up</span>
            <span>Mouse Drag</span>
            <span>Key Down/Up</span>
            <span>Type Text</span>
            <span>Shell + Git</span>
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
          <p className="caption">
            {weather?.stale ? "Показан кэш." : "Актуально."} {weather?.updated_at ?? ""}
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
            В backend теперь есть `mouse_down`, `mouse_up`, `hold`, `drag`, `key_down`,
            `key_up`, `key_press`, `key_hold` и `type_text`.
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
              <h2>Провайдер, модели и шаги</h2>
            </div>
            <button type="submit">Сохранить</button>
          </div>
          <div className="settings-grid">
            <label>
              Base URL
              <input
                value={config.base_url}
                onChange={(event) => setConfig({ ...config, base_url: event.target.value })}
              />
            </label>
            <label>
              API key
              <input
                type="password"
                value={config.api_key}
                onChange={(event) => setConfig({ ...config, api_key: event.target.value })}
              />
            </label>
            <label>
              Text model
              <input
                value={config.text_model}
                onChange={(event) => setConfig({ ...config, text_model: event.target.value })}
              />
            </label>
            <label>
              Vision model
              <input
                value={config.vision_model}
                onChange={(event) => setConfig({ ...config, vision_model: event.target.value })}
              />
            </label>
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
