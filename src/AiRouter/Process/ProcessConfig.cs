namespace AiRouter.Process;

public class ProcessConfig
{
    // Executable path, e.g. "pwsh" or "C:\llama\llama-server.exe"
    public string FileName { get; set; } = string.Empty;

    // Arguments string passed to the process, e.g. "-File C:\llama\start.ps1"
    public string Arguments { get; set; } = string.Empty;

    // Seconds to wait after a fresh start before forwarding the first request (default: 2)
    public int StartupDelaySeconds { get; set; } = 2;
}
