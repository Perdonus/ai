using System.Text.Json.Serialization;

namespace AgentShell.Models;

public sealed class ShellConfig
{
    [JsonPropertyName("local_ai")]
    public LocalAiSettings LocalAi { get; set; } = new();

    [JsonPropertyName("agent")]
    public AgentSettings Agent { get; set; } = new();
}

public sealed class AgentSettings
{
    [JsonPropertyName("max_steps")]
    public int MaxSteps { get; set; } = 40;

    [JsonPropertyName("step_delay_ms")]
    public int StepDelayMs { get; set; } = 700;

    [JsonPropertyName("use_ocr_hints")]
    public bool UseOcrHints { get; set; }

    /// <summary>Clicking the notification area chevron when the agent gets stuck.</summary>
    [JsonPropertyName("tray_recovery")]
    public bool TrayRecovery { get; set; } = true;

    /// <summary>Where the per-step screenshot files are written. Empty means %TEMP%.</summary>
    [JsonPropertyName("screenshot_dir")]
    public string ScreenshotDir { get; set; } = string.Empty;

    [JsonPropertyName("keep_screenshots")]
    public int KeepScreenshots { get; set; } = 40;
}

public sealed class LocalAiSettings
{
    [JsonPropertyName("koboldcpp_path")]
    public string KoboldCppPath { get; set; } = string.Empty;

    [JsonPropertyName("model_path")]
    public string ModelPath { get; set; } = string.Empty;

    [JsonPropertyName("mmproj_path")]
    public string MmprojPath { get; set; } = string.Empty;

    [JsonPropertyName("context_size")]
    public int ContextSize { get; set; } = 8192;

    [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; set; } = 999;

    [JsonPropertyName("quant_kv")]
    public string QuantKv { get; set; } = "q8_0";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 5002;

    [JsonPropertyName("threads")]
    public int Threads { get; set; } = 6;

    [JsonPropertyName("image_max_pixels")]
    public int ImageMaxPixels { get; set; } = 1310720;

    [JsonPropertyName("mmproj_on_cpu")]
    public bool MmprojOnCpu { get; set; }

    [JsonPropertyName("idle_unload_seconds")]
    public int IdleUnloadSeconds { get; set; } = 1800;

    [JsonPropertyName("extra_args")]
    public string ExtraArgs { get; set; } = string.Empty;

    public string Summary =>
        $"ctx {ContextSize} · gpu layers {GpuLayers} · kv {QuantKv} · port {Port}";
}

public sealed class RuntimeItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool HasSettings { get; set; }

    public bool SupportsDataInput { get; set; }

    public string DataInputHint { get; set; } = string.Empty;

    public string RootPath { get; set; } = string.Empty;
}
