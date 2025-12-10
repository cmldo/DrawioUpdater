using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DrawioUpdater
{
    public class MainForm : Form
    {
        private Button updateButton;
        private ProgressBar progressBar;
        private TextBox logBox;

        private readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        private readonly string logFile;
        private readonly string versionLogFile;
        private readonly string sevenZipExe;

        private readonly string portableDir;
        private readonly string desktopVersionFile;
        private readonly string portableVersionFile;

        public MainForm()
        {
            this.Text = "Draw.io Updater";
            this.Width = 600;
            this.Height = 400;

            updateButton = new Button() { Text = "Update Draw.io Portable", Left = 20, Top = 20, Width = 200, Height = 40 };
            updateButton.Click += async (_, __) => await UpdateDrawioAsync();

            progressBar = new ProgressBar() { Left = 20, Top = 70, Width = 540, Height = 25 };
            logBox = new TextBox() { Left = 20, Top = 110, Width = 540, Height = 220, Multiline = true, ScrollBars = ScrollBars.Vertical };

            this.Controls.Add(updateButton);
            this.Controls.Add(progressBar);
            this.Controls.Add(logBox);

            logFile = Path.Combine(baseDir, "update.log");
            versionLogFile = Path.Combine(baseDir, "version.log");
            sevenZipExe = Path.Combine(baseDir, "7zr.exe");

            portableDir = Path.Combine(baseDir, "drawio-portable");
            desktopVersionFile = Path.Combine(portableDir, "installed_desktop_version.txt");
            portableVersionFile = Path.Combine(portableDir, "installed_portable_version.txt");

            this.Load += async (_, __) => await CheckIfUpdateNeededAsync();
        }

        private async Task CheckIfUpdateNeededAsync()
        {
            try
            {
                var desktopUpdate = await CheckUpdateNeededAsync("jgraph", "drawio-desktop", desktopVersionFile);
                var portableUpdate = await CheckUpdateNeededAsync("portapps", "drawio-portable", portableVersionFile);

                if (!desktopUpdate.updateNeeded && !portableUpdate.updateNeeded)
                {
                    LogAction("All components are up to date.");
                    updateButton.Enabled = false;
                }
                else
                {
                    if (desktopUpdate.updateNeeded)
                        LogAction($"drawio-desktop update needed: current {desktopUpdate.currentVersion ?? "none"} → latest {desktopUpdate.latestVersion}");
                    if (portableUpdate.updateNeeded)
                        LogAction($"drawio-portable update needed: current {portableUpdate.currentVersion ?? "none"} → latest {portableUpdate.latestVersion}");
                    updateButton.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                LogAction($"Error checking versions: {ex.Message}");
            }
        }

        private async Task<(bool updateNeeded, string latestVersion, string currentVersion)> CheckUpdateNeededAsync(string owner, string repo, string installedVersionFile)
        {
            string currentVersion = File.Exists(installedVersionFile) ? File.ReadAllText(installedVersionFile).Trim() : null;

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DrawioUpdater");
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

            var response = await client.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            string latestVersion = doc.RootElement.GetProperty("tag_name").GetString();

            bool updateNeeded = currentVersion != latestVersion;
            return (updateNeeded, latestVersion, currentVersion);
        }

        private async Task UpdateDrawioAsync()
        {
            updateButton.Enabled = false;
            try
            {
                LogAction("Starting update...");

                // 1️⃣ Get latest releases
                var drawioDesktop = await GetLatestRelease("jgraph", "drawio-desktop", "*.zip");
                var drawioPortable = await GetLatestRelease("portapps", "drawio-portable", "*.7z");

                LogVersion("drawio-desktop", drawioDesktop.version);
                LogVersion("drawio-portable", drawioPortable.version);

                // 2️⃣ Download files
                string desktopFile = Path.Combine(baseDir, Path.GetFileName(drawioDesktop.url));
                string portableFile = Path.Combine(baseDir, Path.GetFileName(drawioPortable.url));

                await DownloadFileAsync(drawioDesktop.url, desktopFile);
                await DownloadFileAsync(drawioPortable.url, portableFile);

                // 3️⃣ Extract portable 7z
                Directory.CreateDirectory(portableDir);
                await Run7zExtract(portableFile, portableDir);

                // 4️⃣ Delete old app folder
                string appDir = Path.Combine(portableDir, "app");
                if (Directory.Exists(appDir))
                {
                    Directory.Delete(appDir, true);
                    LogAction("Deleted old app folder.");
                }

                // 5️⃣ Extract desktop zip into app folder
                ZipFile.ExtractToDirectory(desktopFile, appDir);
                LogAction("Extracted desktop zip into app folder.");

                // 6️⃣ Save installed versions
                File.WriteAllText(desktopVersionFile, drawioDesktop.version);
                File.WriteAllText(portableVersionFile, drawioPortable.version);

                // 7️⃣ Cleanup downloaded archives
                File.Delete(desktopFile);
                File.Delete(portableFile);
                LogAction("Deleted downloaded archive files.");

                LogAction("Update completed successfully.");
            }
            catch (Exception ex)
            {
                LogAction($"Error: {ex.Message}");
            }
            finally
            {
                updateButton.Enabled = true;
            }
        }

        private async Task<(string version, string url)> GetLatestRelease(string owner, string repo, string assetPattern)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DrawioUpdater");
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

            LogAction($"Fetching latest release for {repo}...");
            var response = await client.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString();
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString();
                if (Regex.IsMatch(name, assetPattern.Replace("*", ".*")))
                {
                    string url = asset.GetProperty("browser_download_url").GetString();
                    LogAction($"Latest {repo} asset found: {name}");
                    return (tagName, url);
                }
            }
            throw new Exception($"No asset matching {assetPattern} found for {repo}");
        }

        private async Task DownloadFileAsync(string url, string destination)
        {
            LogAction($"Downloading {Path.GetFileName(destination)}...");
            using var client = new HttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var canReportProgress = totalBytes != -1;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(destination);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;
                if (canReportProgress)
                    progressBar.Value = (int)((totalRead * 100) / totalBytes);
            }
            progressBar.Value = 0;
            LogAction($"Downloaded {Path.GetFileName(destination)}.");
        }

        private async Task Run7zExtract(string archiveFile, string outputDir)
        {
            if (!File.Exists(sevenZipExe))
                throw new Exception("7zr.exe not found in application folder.");

            LogAction($"Extracting {Path.GetFileName(archiveFile)} to {outputDir}...");
            var psi = new ProcessStartInfo
            {
                FileName = sevenZipExe,
                Arguments = $"x \"{archiveFile}\" -o\"{outputDir}\" -y",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            string output = await process.StandardOutput.ReadToEndAsync();
            string err = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            LogAction(output);
            if (!string.IsNullOrEmpty(err))
                LogAction(err);
        }

        private void LogAction(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            logBox.AppendText(line + Environment.NewLine);
            File.AppendAllText(logFile, line + Environment.NewLine);
        }

        private void LogVersion(string name, string version)
        {
            string line = $"{name}: {version}";
            File.AppendAllText(versionLogFile, line + Environment.NewLine);
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
