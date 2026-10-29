using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;

[assembly: AssemblyTitle("资源清道夫 / AssetScavenger")]
[assembly: AssemblyProduct("AssetScavenger")]
[assembly: AssemblyDescription("Your game, as open source. 你的游戏，在我面前如同开源。")]

namespace AndroidGameUnpacker
{
    internal static class Program
    {
        internal const string AppName = "资源清道夫 / AssetScavenger";
        internal const string LocalUrl = "http://127.0.0.1:18765/";
        private static Mutex _singleInstance;
        private static PipelineServer _server;
        private static NotifyIcon _tray;

        [STAThread]
        private static void Main(string[] args)
        {
            bool noBrowser = args != null && args.Any(delegate(string value) { return string.Equals(value, "--no-browser", StringComparison.OrdinalIgnoreCase); });
            bool created;
            _singleInstance = new Mutex(true, "Local\\AndroidGameUnpackerPipeline", out created);
            if (!created)
            {
                if (!noBrowser) OpenBrowser();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (!File.Exists(Path.Combine(baseDirectory, "Run-Unpack.ps1")))
                {
                    MessageBox.Show("EXE 必须放在 Run-Unpack.ps1 所在目录。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _server = new PipelineServer(baseDirectory);
                _server.Start();
                CreateTrayIcon();
                if (!noBrowser) OpenBrowser();
                Application.Run();
            }
            catch (SocketException ex)
            {
                MessageBox.Show("无法启动本地网站：" + ex.Message + "\n端口 18765 可能被其他程序占用。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_server != null) { _server.Dispose(); }
                if (_singleInstance != null) { _singleInstance.Dispose(); }
            }
        }

        private static void CreateTrayIcon()
        {
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("打开控制台", delegate { OpenBrowser(); });
            menu.MenuItems.Add("打开最近输出", delegate
            {
                string path = _server == null ? null : _server.CurrentJobRoot;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    Process.Start("explorer.exe", "\"" + path + "\"");
            });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", delegate
            {
                if (_server != null && _server.IsRunning)
                {
                    DialogResult result = MessageBox.Show("解包任务仍在运行。停止任务并退出吗？", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (result != DialogResult.Yes) return;
                    _server.StopJob();
                }
                Application.Exit();
            });

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "资源清道夫 AssetScavenger";
            _tray.ContextMenu = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { OpenBrowser(); };
        }

        private static void OpenBrowser()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(LocalUrl);
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch { }
        }
    }

    internal sealed class PipelineServer : IDisposable
    {
        private readonly string _baseDirectory;
        private readonly TcpListener _listener;
        private Thread _listenerThread;
        private readonly JavaScriptSerializer _json;
        private readonly object _sync = new object();
        private readonly string _token;
        private PipelineState _state;
        private Process _process;
        private bool _disposed;

        public PipelineServer(string baseDirectory)
        {
            _baseDirectory = baseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            _listener = new TcpListener(IPAddress.Loopback, 18765);
            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = 32 * 1024 * 1024;
            _token = CreateToken();
            _state = PipelineState.CreateIdle();
        }

        public bool IsRunning
        {
            get { lock (_sync) return _state.Status == "running" || _state.Status == "stopping"; }
        }

        public string CurrentJobRoot
        {
            get { lock (_sync) return _state.JobRoot; }
        }

        public void Start()
        {
            _listener.Start();
            _listenerThread = new Thread(new ThreadStart(AcceptLoop));
            _listenerThread.IsBackground = true;
            _listenerThread.Name = "PipelineLocalHttpServer";
            _listenerThread.Start();
        }

        private void AcceptLoop()
        {
            while (!_disposed)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
                catch (SocketException) { if (!_disposed) Thread.Sleep(100); }
                catch (ObjectDisposedException) { return; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;
                    LocalHttpContext context = LocalHttpContext.Read(client);
                    if (context != null) HandleRequest(context);
                }
                catch { }
            }
        }

        private void HandleRequest(LocalHttpContext context)
        {
            try
            {
                string path = context.Request.Path.ToLowerInvariant();
                if (context.Request.Method == "GET" && path == "/")
                {
                    WriteHtml(context, LoadHtml().Replace("{{PIPELINE_TOKEN}}", _token));
                    return;
                }
                if (context.Request.Method == "GET" && path == "/api/status")
                {
                    WriteJson(context, GetSnapshot());
                    return;
                }
                if (context.Request.Method == "GET" && path == "/api/recent")
                {
                    WriteJson(context, GetRecentJobs());
                    return;
                }
                if (context.Request.Method == "GET" && path == "/favicon.ico")
                {
                    context.Write(new byte[0], "image/x-icon", 204);
                    return;
                }
                if (context.Request.Method == "POST")
                {
                    if (!IsAuthorized(context))
                    {
                        WriteJson(context, new { ok = false, error = "请求令牌无效。" }, 403);
                        return;
                    }
                    if (path == "/api/select-file")
                    {
                        string selected = DialogHelper.SelectFile();
                        WriteJson(context, new { ok = true, cancelled = string.IsNullOrEmpty(selected), path = selected });
                        return;
                    }
                    if (path == "/api/select-folder")
                    {
                        string selected = DialogHelper.SelectFolder();
                        WriteJson(context, new { ok = true, cancelled = string.IsNullOrEmpty(selected), path = selected });
                        return;
                    }
                    if (path == "/api/select-output")
                    {
                        string selected = DialogHelper.SelectOutputFolder();
                        WriteJson(context, new { ok = true, cancelled = string.IsNullOrEmpty(selected), path = selected, disk = GetDiskInfo(selected) });
                        return;
                    }
                    if (path == "/api/disk-info")
                    {
                        PathRequest request = ReadJson<PathRequest>(context);
                        WriteJson(context, new { ok = true, disk = GetDiskInfo(request == null ? null : request.Path) });
                        return;
                    }
                    if (path == "/api/start")
                    {
                        StartRequest request = ReadJson<StartRequest>(context);
                        string error = StartJob(request);
                        WriteJson(context, new { ok = string.IsNullOrEmpty(error), error = error }, string.IsNullOrEmpty(error) ? 200 : 400);
                        return;
                    }
                    if (path == "/api/stop")
                    {
                        StopJob();
                        WriteJson(context, new { ok = true });
                        return;
                    }
                    if (path == "/api/open-output")
                    {
                        string jobRoot = CurrentJobRoot;
                        if (!string.IsNullOrEmpty(jobRoot) && Directory.Exists(jobRoot)) Process.Start("explorer.exe", "\"" + jobRoot + "\"");
                        WriteJson(context, new { ok = true });
                        return;
                    }
                    if (path == "/api/open-report")
                    {
                        string jobRoot = CurrentJobRoot;
                        string report = string.IsNullOrEmpty(jobRoot) ? null : Path.Combine(jobRoot, "00_report", "report.md");
                        if (!string.IsNullOrEmpty(report) && File.Exists(report)) Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
                        WriteJson(context, new { ok = true });
                        return;
                    }
                }
                WriteJson(context, new { ok = false, error = "Not found" }, 404);
            }
            catch (Exception ex)
            {
                try { WriteJson(context, new { ok = false, error = ex.Message }, 500); } catch { }
            }
        }

        private bool IsAuthorized(LocalHttpContext context)
        {
            string header = context.Request.GetHeader("X-Pipeline-Token");
            if (!string.Equals(header, _token, StringComparison.Ordinal)) return false;
            string origin = context.Request.GetHeader("Origin");
            if (!string.IsNullOrEmpty(origin) && !string.Equals(origin.TrimEnd('/'), Program.LocalUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private string StartJob(StartRequest request)
        {
            if (request == null) return "请求内容为空。";
            string inputUrl = (request.InputUrl ?? "").Trim();
            string inputPath = (request.InputPath ?? "").Trim();
            bool urlMode = inputUrl.Length > 0;
            Uri parsedUrl;
            if (urlMode && (!Uri.TryCreate(inputUrl, UriKind.Absolute, out parsedUrl) || (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))) return "网页地址必须是完整的 http:// 或 https:// URL。";
            if (!urlMode && inputPath.Length == 0) return "请粘贴网页游戏网址，或选择本地文件/目录。";
            if (!urlMode && !File.Exists(inputPath) && !Directory.Exists(inputPath)) return "输入路径不存在。";
            string script = Path.Combine(_baseDirectory, "Run-Unpack.ps1");
            if (!File.Exists(script)) return "找不到 Run-Unpack.ps1。";

            string engine = Allowed(request.Engine, new[] { "auto", "unity", "unreal", "web", "unknown" }, "auto");
            if (urlMode && engine != "web") return "网址输入只适用于“网页小游戏 / 4399”页面。";
            string profile = Allowed(request.Profile, new[] { "scan", "standard", "full", "unityproject" }, "standard");
            bool ordinaryExport = profile == "standard" || profile == "full";
            if (profile == "unityproject" && engine != "auto" && engine != "unity") return "Unity 工程重建只适用于 Unity 输入。";
            List<string> kinds = (request.AssetKinds ?? new string[0]).Where(delegate(string value)
            {
                return new[] { "images", "audio", "models", "text" }.Contains((value ?? "").ToLowerInvariant());
            }).Select(delegate(string value) { return value.ToLowerInvariant(); }).Distinct().ToList();
            if (ordinaryExport && kinds.Count == 0) return "至少选择一种要导出的资源。";
            List<string> modelFormats = (request.ModelFormats ?? new string[0]).Where(delegate(string value)
            {
                return new[] { "obj", "fbx", "gltf" }.Contains((value ?? "").ToLowerInvariant());
            }).Select(delegate(string value) { return value.ToLowerInvariant(); }).Distinct().ToList();
            if (ordinaryExport && kinds.Contains("models") && modelFormats.Count == 0) return "导出模型时至少选择 OBJ、FBX 或 GLTF 中的一种格式。";
            if (profile != "scan" && !request.ConfirmLargeExport) return "请先确认导出可能占用大量磁盘空间。";

            string outputRoot = string.IsNullOrWhiteSpace(request.OutputPath) ? Path.Combine(_baseDirectory, "jobs") : request.OutputPath.Trim();
            try { outputRoot = Path.GetFullPath(outputRoot); }
            catch { return "导出路径无效。"; }
            if (File.Exists(outputRoot)) return "导出路径指向一个文件，请选择目录。";
            if (!urlMode && Directory.Exists(inputPath))
            {
                string inputFull = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string outputFull = outputRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (outputFull.StartsWith(inputFull, StringComparison.OrdinalIgnoreCase)) return "导出目录不能放在输入游戏目录内部，否则扫描时可能重复读取导出文件。";
            }
            try { Directory.CreateDirectory(outputRoot); }
            catch (Exception ex) { return "无法创建导出目录：" + ex.Message; }
            string writeProbe = Path.Combine(outputRoot, ".assetscavenger-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream probe = new FileStream(writeProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) probe.WriteByte(0);
            }
            catch (Exception ex)
            {
                return "导出目录不可写，请重新点击“选择”并换一个有权限的目录。Windows 返回：" + ex.Message;
            }
            finally
            {
                try { if (File.Exists(writeProbe)) File.Delete(writeProbe); } catch { }
            }

            lock (_sync)
            {
                if (_state.Status == "running" || _state.Status == "stopping") return "已有任务正在运行。";
                _state = PipelineState.CreateRunning(urlMode ? inputUrl : inputPath, outputRoot, engine, profile, kinds.ToArray(), modelFormats.ToArray());
                _state.PlannedPhases = profile == "unityproject" ? 2 : 1 + (profile == "full" ? 1 : 0) + (profile == "scan" ? 0 : kinds.Count) + (ordinaryExport && kinds.Contains("models") && modelFormats.Any(delegate(string value) { return value != "obj"; }) ? 1 : 0);
            }

            Thread worker = new Thread(new ThreadStart(delegate { RunJob(script, inputPath, inputUrl, request.BuildPlayableWeb, outputRoot, engine, profile, kinds, modelFormats, request.UnityVersion, request.MaxExportTasks); }));
            worker.IsBackground = true;
            worker.Name = "UnpackPipelineWorker";
            worker.Start();
            return null;
        }

        private void RunJob(string script, string inputPath, string inputUrl, bool buildPlayableWeb, string outputRoot, string engine, string profile, List<string> kinds, List<string> modelFormats, string unityVersion, int maxTasks)
        {
            try
            {
                StringBuilder command = new StringBuilder();
                command.Append("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); $OutputEncoding = [Console]::OutputEncoding; $ProgressPreference = 'SilentlyContinue'; ");
                command.Append("& ").Append(PsLiteral(script));
                if (!string.IsNullOrWhiteSpace(inputUrl)) command.Append(" -InputUrl ").Append(PsLiteral(inputUrl));
                else command.Append(" -InputPath ").Append(PsLiteral(inputPath));
                if (buildPlayableWeb) command.Append(" -BuildPlayableWeb");
                command.Append(" -OutputRoot ").Append(PsLiteral(outputRoot));
                command.Append(" -Engine ").Append(PsLiteral(engine));
                command.Append(" -Profile ").Append(PsLiteral(profile));
                if (kinds.Count > 0)
                {
                    command.Append(" -AssetKinds @(");
                    command.Append(string.Join(",", kinds.Select(PsLiteral).ToArray()));
                    command.Append(")");
                }
                if (modelFormats.Count > 0)
                {
                    command.Append(" -ModelFormats @(");
                    command.Append(string.Join(",", modelFormats.Select(PsLiteral).ToArray()));
                    command.Append(")");
                }
                if (!string.IsNullOrWhiteSpace(unityVersion)) command.Append(" -UnityVersion ").Append(PsLiteral(unityVersion.Trim()));
                if (maxTasks > 0 && maxTasks <= 128) command.Append(" -MaxExportTasks ").Append(maxTasks);

                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command.ToString()));
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = "powershell.exe";
                start.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
                start.WorkingDirectory = _baseDirectory;
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.StandardOutputEncoding = Encoding.UTF8;
                start.StandardErrorEncoding = Encoding.Default;

                Process process = new Process();
                process.StartInfo = start;
                process.EnableRaisingEvents = true;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) AppendLog(args.Data, false); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) AppendLog(args.Data, true); };
                lock (_sync) _process = process;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit(1000);
                int code = process.ExitCode;

                lock (_sync)
                {
                    if (_state.Status == "stopping")
                    {
                        _state.Status = "cancelled";
                        _state.Stage = "任务已停止";
                    }
                    else if (code == 0)
                    {
                        _state.Status = "completed";
                        _state.Stage = "解包完成";
                        _state.Progress = 100;
                    }
                    else
                    {
                        _state.Status = "failed";
                        _state.Stage = "运行失败，退出码 " + code;
                        _state.Error = "PowerShell 返回退出码 " + code;
                    }
                    _state.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    _process = null;
                    LoadReportLocked();
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _state.Status = "failed";
                    _state.Stage = "启动失败";
                    _state.Error = ex.Message;
                    _state.Logs.Add("[错误] " + ex);
                    _state.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    _process = null;
                }
            }
        }

        private void AppendLog(string line, bool isError)
        {
            if (line.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase)) return;
            if (line.StartsWith("<Objs ", StringComparison.OrdinalIgnoreCase))
            {
                List<string> messages = new List<string>();
                foreach (Match match in Regex.Matches(line, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline))
                {
                    string message = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Replace("_x000D__x000A_", " ").Trim();
                    if (message.Length > 0 && !messages.Contains(message)) messages.Add(message);
                }
                if (messages.Count == 0) return;
                line = string.Join(" ", messages.ToArray());
            }
            lock (_sync)
            {
                string cleaned = Regex.Replace(line, "\\x1B\\[[0-9;?]*[ -/]*[@-~]", "");
                _state.Logs.Add((isError ? "[stderr] " : "") + cleaned);
                if (_state.Logs.Count > 6000) _state.Logs.RemoveRange(0, 1000);

                Match pipelinePercent = Regex.Match(cleaned, "\\[进度\\s+(\\d{1,3})%\\]");
                if (pipelinePercent.Success)
                {
                    int value;
                    if (int.TryParse(pipelinePercent.Groups[1].Value, out value))
                    {
                        _state.UsesStructuredProgress = true;
                        _state.Progress = Math.Max(_state.Progress, Math.Max(0, Math.Min(100, value)));
                    }
                }
                Match scriptStage = Regex.Match(cleaned, "\\[脚本\\s+([^\\]]+)\\]\\s*(.+)$");
                if (scriptStage.Success)
                {
                    string stageText = scriptStage.Groups[2].Value.Trim().TrimEnd('.');
                    _state.Stage = stageText + " · " + scriptStage.Groups[1].Value.Trim();
                }

                Match job = Regex.Match(cleaned, "任务目录[：:]\\s*(.+)$");
                if (job.Success) _state.JobRoot = job.Groups[1].Value.Trim().TrimEnd('.');
                Match running = Regex.Match(cleaned, "运行\\s+(.+)$");
                if (running.Success)
                {
                    _state.PhaseIndex++;
                    _state.Stage = running.Groups[1].Value.Trim();
                    UpdateProgressLocked(0);
                }
                Match percent = Regex.Match(cleaned, "\\[(\\d{1,3})%\\]");
                if (percent.Success && !_state.UsesStructuredProgress)
                {
                    int value;
                    if (int.TryParse(percent.Groups[1].Value, out value)) UpdateProgressLocked(Math.Max(0, Math.Min(100, value)));
                }
                if (cleaned.Contains("安全展开 APK")) { _state.Stage = "正在展开 APK"; _state.Progress = Math.Max(_state.Progress, 3); }
                if (cleaned.Contains("识别引擎：")) { _state.Stage = cleaned.Substring(cleaned.IndexOf("识别引擎：", StringComparison.Ordinal)); }
                if (cleaned.Contains("完成。图片")) _state.Stage = cleaned.Substring(cleaned.IndexOf("完成。", StringComparison.Ordinal));
            }
        }

        private void UpdateProgressLocked(int phasePercent)
        {
            int phases = Math.Max(1, _state.PlannedPhases);
            int phase = Math.Max(0, _state.PhaseIndex);
            double completed = phase + phasePercent / 100.0;
            int progress = 5 + (int)Math.Floor(Math.Min(phases, completed) / phases * 90.0);
            _state.Progress = Math.Max(_state.Progress, Math.Min(95, progress));
        }

        public void StopJob()
        {
            Process process = null;
            lock (_sync)
            {
                if (_state.Status != "running") return;
                _state.Status = "stopping";
                _state.Stage = "正在停止任务…";
                process = _process;
            }
            if (process != null)
            {
                try
                {
                    ProcessStartInfo kill = new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F");
                    kill.UseShellExecute = false;
                    kill.CreateNoWindow = true;
                    Process killer = Process.Start(kill);
                    if (killer != null) killer.WaitForExit(5000);
                }
                catch { try { process.Kill(); } catch { } }
            }
        }

        private object GetSnapshot()
        {
            lock (_sync)
            {
                return new
                {
                    status = _state.Status,
                    stage = _state.Stage,
                    progress = _state.Progress,
                    inputPath = _state.InputPath,
                    outputRoot = _state.OutputRoot,
                    defaultOutput = Path.Combine(_baseDirectory, "jobs"),
                    engine = _state.Engine,
                    profile = _state.Profile,
                    assetKinds = _state.AssetKinds,
                    modelFormats = _state.ModelFormats,
                    jobRoot = _state.JobRoot,
                    startedAt = _state.StartedAt,
                    finishedAt = _state.FinishedAt,
                    error = _state.Error,
                    logs = _state.Logs.ToArray(),
                    report = _state.Report
                };
            }
        }

        private object GetRecentJobs()
        {
            string jobs = Path.Combine(_baseDirectory, "jobs");
            if (!Directory.Exists(jobs)) return new object[0];
            return Directory.GetDirectories(jobs)
                .Select(delegate(string path) { return new DirectoryInfo(path); })
                .OrderByDescending(delegate(DirectoryInfo info) { return info.LastWriteTime; })
                .Take(10)
                .Select(delegate(DirectoryInfo info)
                {
                    string reportPath = Path.Combine(info.FullName, "00_report", "report.json");
                    object report = null;
                    try { if (File.Exists(reportPath)) report = _json.DeserializeObject(File.ReadAllText(reportPath, Encoding.UTF8)); } catch { }
                    return new { name = info.Name, path = info.FullName, modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), report = report };
                }).ToArray();
        }

        private object GetDiskInfo(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                DriveInfo drive = new DriveInfo(root);
                return new { root = root, freeBytes = drive.AvailableFreeSpace, totalBytes = drive.TotalSize, freeText = FormatBytes(drive.AvailableFreeSpace), totalText = FormatBytes(drive.TotalSize) };
            }
            catch { return null; }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int index = 0;
            while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
            return value.ToString(index == 0 ? "0" : "0.0") + " " + units[index];
        }

        private void LoadReportLocked()
        {
            if (string.IsNullOrEmpty(_state.JobRoot)) return;
            string path = Path.Combine(_state.JobRoot, "00_report", "report.json");
            try { if (File.Exists(path)) _state.Report = _json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception ex) { _state.Logs.Add("[报告] 无法读取 report.json：" + ex.Message); }
        }

        private T ReadJson<T>(LocalHttpContext context)
        {
            using (StreamReader reader = new StreamReader(new MemoryStream(context.Request.Body), Encoding.UTF8))
                return _json.Deserialize<T>(reader.ReadToEnd());
        }

        private void WriteJson(LocalHttpContext context, object value, int statusCode)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_json.Serialize(value));
            context.Write(bytes, "application/json; charset=utf-8", statusCode);
        }

        private void WriteJson(LocalHttpContext context, object value) { WriteJson(context, value, 200); }

        private static void WriteHtml(LocalHttpContext context, string html)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            context.Write(bytes, "text/html; charset=utf-8", 200);
        }

        private static string LoadHtml()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PipelineWeb.IndexHtml"))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static string CreateToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static string PsLiteral(string value) { return "'" + (value ?? "").Replace("'", "''") + "'"; }

        private static string Allowed(string value, string[] allowed, string fallback)
        {
            string normalized = (value ?? "").ToLowerInvariant();
            return allowed.Contains(normalized) ? normalized : fallback;
        }

        public void Dispose()
        {
            _disposed = true;
            try { _listener.Stop(); } catch { }
        }
    }

    internal sealed class LocalHttpRequest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public byte[] Body { get; set; }

        public string GetHeader(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }
    }

    internal sealed class LocalHttpContext
    {
        private readonly TcpClient _client;
        public LocalHttpRequest Request { get; private set; }

        private LocalHttpContext(TcpClient client, LocalHttpRequest request)
        {
            _client = client;
            Request = request;
        }

        public static LocalHttpContext Read(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            MemoryStream received = new MemoryStream();
            byte[] buffer = new byte[8192];
            int headerEnd = -1;
            int contentLength = 0;
            while (received.Length < 16 * 1024 * 1024)
            {
                int count = stream.Read(buffer, 0, buffer.Length);
                if (count <= 0) return null;
                received.Write(buffer, 0, count);
                byte[] current = received.ToArray();
                if (headerEnd < 0)
                {
                    headerEnd = FindHeaderEnd(current);
                    if (headerEnd >= 0)
                    {
                        string headerText = Encoding.ASCII.GetString(current, 0, headerEnd);
                        Match length = Regex.Match(headerText, "(?im)^Content-Length:\\s*(\\d+)\\s*$");
                        if (length.Success) int.TryParse(length.Groups[1].Value, out contentLength);
                        if (contentLength > 12 * 1024 * 1024) throw new InvalidDataException("请求正文过大。");
                    }
                }
                if (headerEnd >= 0 && current.Length >= headerEnd + 4 + contentLength)
                {
                    string headerText = Encoding.ASCII.GetString(current, 0, headerEnd);
                    string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] first = lines[0].Split(' ');
                    if (first.Length < 2) return null;
                    Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                    }
                    byte[] body = new byte[contentLength];
                    if (contentLength > 0) Buffer.BlockCopy(current, headerEnd + 4, body, 0, contentLength);
                    string target = first[1];
                    int query = target.IndexOf('?');
                    if (query >= 0) target = target.Substring(0, query);
                    return new LocalHttpContext(client, new LocalHttpRequest { Method = first[0].ToUpperInvariant(), Path = Uri.UnescapeDataString(target), Headers = headers, Body = body });
                }
            }
            throw new InvalidDataException("HTTP 请求过大。 ");
        }

        public void Write(byte[] body, string contentType, int statusCode)
        {
            string status = statusCode == 200 ? "OK" : statusCode == 204 ? "No Content" : statusCode == 400 ? "Bad Request" : statusCode == 403 ? "Forbidden" : statusCode == 404 ? "Not Found" : "Internal Server Error";
            string header = "HTTP/1.1 " + statusCode + " " + status + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Connection: close\r\n\r\n";
            NetworkStream stream = _client.GetStream();
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static int FindHeaderEnd(byte[] bytes)
        {
            for (int i = 0; i <= bytes.Length - 4; i++)
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
            return -1;
        }
    }

    internal static class DialogHelper
    {
        public static string SelectFile()
        {
            return RunOnForegroundSta(delegate(IWin32Window owner)
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Title = "选择 APK、Unity/Unreal 资源包或网页小游戏";
                    dialog.Filter = "支持的游戏文件|*.apk;*.ab;*.bundle;*.unity3d;*.assets;*.pak;*.swf;*.zip;*.html;*.htm|Android APK|*.apk|Unity 资源|*.ab;*.bundle;*.unity3d;*.assets|Unreal PAK|*.pak|网页小游戏|*.swf;*.zip;*.html;*.htm|所有文件|*.*";
                    dialog.CheckFileExists = true;
                    dialog.RestoreDirectory = true;
                    return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
                }
            });
        }

        public static string SelectFolder()
        {
            return RunOnForegroundSta(delegate(IWin32Window owner)
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择已解包 APK、Unity、Unreal 或 HTML5 网页小游戏目录";
                    dialog.ShowNewFolderButton = false;
                    return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
                }
            });
        }

        public static string SelectOutputFolder()
        {
            return RunOnForegroundSta(delegate(IWin32Window owner)
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择资源导出位置。完整游戏可能导出数十 GB，请选择空间充足的磁盘。";
                    dialog.ShowNewFolderButton = true;
                    return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
                }
            });
        }

        private static string RunOnForegroundSta(Func<IWin32Window, string> showDialog)
        {
            string selected = null;
            Exception failure = null;
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    using (Form owner = new Form())
                    {
                        owner.Text = Program.AppName;
                        owner.ShowInTaskbar = false;
                        owner.TopMost = true;
                        owner.FormBorderStyle = FormBorderStyle.None;
                        owner.StartPosition = FormStartPosition.CenterScreen;
                        owner.Size = new Size(2, 2);
                        owner.Opacity = 0.01;
                        owner.Shown += delegate
                        {
                            owner.Activate();
                            owner.BringToFront();
                            selected = showDialog(owner);
                            owner.Close();
                        };
                        Application.Run(owner);
                    }
                }
                catch (Exception ex) { failure = ex; }
            }));
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "PipelineNativePicker";
            thread.Start();
            thread.Join();
            if (failure != null) throw new InvalidOperationException("无法打开 Windows 选择框：" + failure.Message, failure);
            return selected;
        }
    }

    internal sealed class StartRequest
    {
        public string InputPath { get; set; }
        public string InputUrl { get; set; }
        public string OutputPath { get; set; }
        public string Engine { get; set; }
        public string Profile { get; set; }
        public string[] AssetKinds { get; set; }
        public string[] ModelFormats { get; set; }
        public string UnityVersion { get; set; }
        public int MaxExportTasks { get; set; }
        public bool ConfirmLargeExport { get; set; }
        public bool BuildPlayableWeb { get; set; }
    }

    internal sealed class PathRequest
    {
        public string Path { get; set; }
    }

    internal sealed class PipelineState
    {
        public string Status { get; set; }
        public string Stage { get; set; }
        public int Progress { get; set; }
        public string InputPath { get; set; }
        public string OutputRoot { get; set; }
        public string Engine { get; set; }
        public string Profile { get; set; }
        public string[] AssetKinds { get; set; }
        public string[] ModelFormats { get; set; }
        public string JobRoot { get; set; }
        public string StartedAt { get; set; }
        public string FinishedAt { get; set; }
        public string Error { get; set; }
        public List<string> Logs { get; set; }
        public object Report { get; set; }
        public int PlannedPhases { get; set; }
        public int PhaseIndex { get; set; }
        public bool UsesStructuredProgress { get; set; }

        public static PipelineState CreateIdle()
        {
            return new PipelineState { Status = "idle", Stage = "等待导入游戏文件", Progress = 0, AssetKinds = new string[0], Logs = new List<string>(), PhaseIndex = -1 };
        }

        public static PipelineState CreateRunning(string input, string outputRoot, string engine, string profile, string[] kinds, string[] modelFormats)
        {
            return new PipelineState
            {
                Status = "running", Stage = "正在启动流水线…", Progress = 1, InputPath = input, OutputRoot = outputRoot, Engine = engine, Profile = profile,
                AssetKinds = kinds, ModelFormats = modelFormats, Logs = new List<string>(), StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), PhaseIndex = -1
            };
        }
    }
}
