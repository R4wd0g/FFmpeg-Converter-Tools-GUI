using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FFmpegConverterGUI
{
    public partial class MainWindow : Window
    {
        private const string InstallRegistrySubkey = @"Software\R4wd0G\FFmpeg Converter Tools GUI";
        private const string InstallRegistryValueName = "InstallMode";
        private const string BtbNFfmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-06-05-13-55/ffmpeg-N-124841-gb355200263-win64-gpl.zip";
        private const string GitHubLatestReleaseApiUrl = "https://api.github.com/repos/R4wd0g/FFmpeg-Converter-Tools-GUI/releases/latest";
        private const string GitHubApiUserAgent = "FFmpeg-Converter-Tools-GUI-Updater";
        private readonly object processLock = new object();
        private readonly object updateLock = new object();
        private readonly Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string settingsPath;
        private BackgroundWorker worker;
        private Process currentProcess;
        private MediaPlayer completionSoundPlayer;
        private MediaPlayer creditsSoundPlayer;
        private DispatcherTimer creditsFadeOutTimer;
        private EventHandler creditsLoopHandler;
        private volatile bool cancelRequested;
        private volatile bool updateCheckInProgress;
        private bool darkTheme = true;
        private bool startupUpdateCheckStarted;

        public MainWindow()
        {
            settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
            InitializeComponent();
            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            DropArea.PreviewDragOver += DropArea_PreviewDragOver;
            DropArea.Drop += DropArea_Drop;
            ToolSelector.SelectionChanged += ToolSelector_SelectionChanged;
            ConvertTypeSelector.SelectionChanged += ConvertTypeSelector_SelectionChanged;
            AddButton.Click += AddButton_Click;
            FormatSelector.SelectionChanged += FormatSelector_SelectionChanged;
            ClearButton.Click += ClearButton_Click;
            ProcessButton.Click += ProcessButton_Click;
            CancelButton.Click += CancelButton_Click;
            Closing += MainWindow_Closing;
            ToolSelector_SelectionChanged(this, null);
            LoadSettings();
            ApplyTheme(GetSetting("Theme", "Dark").Equals("Dark", StringComparison.OrdinalIgnoreCase), false);
            RefreshConvertFormats();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            TryUseDarkTitleBar(this, darkTheme);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (startupUpdateCheckStarted)
            {
                return;
            }

            startupUpdateCheckStarted = true;
            BeginCheckForUpdates(false, this);
        }

        private void ToolSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string selected = GetSelectedTool();
            CompressParams.Visibility = selected == "Compress" ? Visibility.Visible : Visibility.Collapsed;
            ConvertParams.Visibility = selected == "Convert" ? Visibility.Visible : Visibility.Collapsed;
            CutParams.Visibility = selected == "Cut" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ConvertTypeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshConvertFormats();
            UpdateGifOptionsVisibility();
        }
        private void FormatSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateGifOptionsVisibility();
        }

        private void DropArea_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropArea_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFiles();
        }

        private void OpenFilesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenFiles();
        }

        private void ClearQueueMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ClearQueue();
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void MinimizeTitleButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseTitleButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowAboutWindow();
        }

        private void CheckForUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            BeginCheckForUpdates(true, this);
        }

        private void DarkThemeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(true);
        }

        private void LightThemeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ApplyTheme(false);
        }

        private void OpenFiles()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Multiselect = true;
            if (dlg.ShowDialog() == true)
            {
                AddFiles(dlg.FileNames);
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearQueue();
        }

        private void ClearQueue()
        {
            FilesList.Items.Clear();
            Log("Queue cleared.");
        }

        private void AddFiles(string[] files)
        {
            int added = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (File.Exists(files[i]) && !FilesList.Items.Contains(files[i]))
                {
                    FilesList.Items.Add(files[i]);
                    added++;
                }
            }

            Log("Files added: " + added);
        }

        private void ProcessButton_Click(object sender, RoutedEventArgs e)
        {
            if (FilesList.Items.Count == 0)
            {
                Log("No files in the queue.");
                return;
            }

            string selectedTool = GetSelectedTool();
            string[] items = GetQueuedFiles();
            cancelRequested = false;
            SetProcessingState(true);

            worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.WorkerSupportsCancellation = true;
            worker.DoWork += Worker_DoWork;
            worker.ProgressChanged += Worker_ProgressChanged;
            worker.RunWorkerCompleted += Worker_RunWorkerCompleted;
            worker.RunWorkerAsync(new JobRequest(selectedTool, items));
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            RequestCancel("Cancel requested.");
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (worker != null && worker.IsBusy)
            {
                RequestCancel("Application is closing. Stopping active FFmpeg process.");
            }

            StopCreditsSound();

            if (completionSoundPlayer != null)
            {
                completionSoundPlayer.Stop();
                completionSoundPlayer.Close();
                completionSoundPlayer = null;
            }
        }

        private void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            JobRequest request = (JobRequest)e.Argument;
            string ffmpegPath = EnsureFFmpegExists();
            if (ffmpegPath == null)
            {
                Log("FFmpeg was not found and could not be installed. Processing canceled.");
                e.Cancel = true;
                return;
            }

            if (cancelRequested)
            {
                e.Cancel = true;
                return;
            }

            if (request.SelectedTool == "Merge")
            {
                ProcessConcat(ffmpegPath, request.Files);
                ReportProgress(100);
            }
            else
            {
                for (int i = 0; i < request.Files.Length; i++)
                {
                    if (cancelRequested)
                    {
                        e.Cancel = true;
                        return;
                    }

                    ProcessSingleFile(ffmpegPath, request.SelectedTool, request.Files[i], i + 1, request.Files.Length);
                    ReportProgress(((i + 1) * 100) / request.Files.Length);
                }
            }

            if (cancelRequested)
            {
                e.Cancel = true;
            }
        }

        private void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            JobProgressBar.Value = e.ProgressPercentage;
        }

        private void Worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                Log("Processing canceled.");
            }
            else if (e.Error != null)
            {
                Log("Processing failed: " + e.Error.Message);
            }
            else
            {
                Log("Processing finished.");
                PlayCompletionSound();
            }

            SetProcessingState(false);
            worker = null;
        }

        private void ProcessSingleFile(string ffmpegPath, string selectedTool, string inputFile, int current, int total)
        {
            if (!File.Exists(inputFile))
            {
                Log("File not found: " + inputFile);
                return;
            }

            Log("Processing " + current.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + ": " + Path.GetFileName(inputFile));

            if (selectedTool == "Compress")
            {
                CompressVideo(ffmpegPath, inputFile);
            }
            else if (selectedTool == "Convert")
            {
                ConvertToSelectedFormat(ffmpegPath, inputFile);
            }
            else if (selectedTool == "Cut")
            {
                CutFile(ffmpegPath, inputFile, IsCutAudioSelected());
            }
            else if (selectedTool == "Remux / Repair")
            {
                RemuxRepair(ffmpegPath, inputFile);
            }
            else if (selectedTool == "Remove Subtitles")
            {
                if (!HasExtension(inputFile, ".mkv") && !HasExtension(inputFile, ".mp4"))
                {
                    Log("Remove Subtitles accepts MKV and MP4 files only: " + Path.GetFileName(inputFile));
                    return;
                }

                string extension = Path.GetExtension(inputFile);
                ReplaceWithTranscode(ffmpegPath, inputFile, "_temp" + extension, "-map 0 -map -0:s -c copy", "_remux" + extension);
            }
        }

        private void CompressVideo(string ffmpegPath, string inputFile)
        {
            int crf = GetCompressionCrf();
            if (HasExtension(inputFile, ".mkv"))
            {
                ReplaceWithTranscode(ffmpegPath, inputFile, "_temp.mkv", "-c:v libx265 -crf " + crf.ToString(CultureInfo.InvariantCulture) + " -vtag hvc1 -c:a copy", "_remux.mkv");
            }
            else if (HasExtension(inputFile, ".mp4"))
            {
                ReplaceWithTranscode(ffmpegPath, inputFile, "_temp.mp4", "-c:v libx265 -crf " + crf.ToString(CultureInfo.InvariantCulture) + " -vtag hvc1 -c:a copy -threads 1", "_remux.mp4");
            }
            else
            {
                Log("Compress Video currently supports MKV and MP4 files: " + Path.GetFileName(inputFile));
            }
        }

        private void ConvertToSelectedFormat(string ffmpegPath, string inputFile)
        {
            string format = GetSelectedFormat();
            if (IsConvertAudioSelected())
            {
                if (format == "MP3")
                {
                    ConvertAudio(ffmpegPath, inputFile, ".mp3", "-vn -acodec libmp3lame");
                }
                else if (format == "WAV")
                {
                    ConvertAudio(ffmpegPath, inputFile, ".wav", "-vn -acodec pcm_s16le -ac 1 -ar 16000");
                }
                else if (format == "OGG")
                {
                    ConvertAudio(ffmpegPath, inputFile, ".ogg", "-vn -acodec libvorbis");
                }
                else if (format == "FLAC")
                {
                    ConvertAudio(ffmpegPath, inputFile, ".flac", "-vn -acodec flac");
                }
                else if (format == "M4A")
                {
                    ConvertAudio(ffmpegPath, inputFile, ".m4a", "-vn -acodec aac -b:a 192k");
                }
                else if (format == "OPUS")
                {
                    ConvertAudio(ffmpegPath, inputFile, ".opus", "-vn -acodec libopus");
                }
                return;
            }

            if (IsConvertImageSelected())
            {
                if (format == "PNG")
                {
                    ConvertImage(ffmpegPath, inputFile, ".png", "-frames:v 1");
                }
                else if (format == "JPG")
                {
                    ConvertImage(ffmpegPath, inputFile, ".jpg", "-frames:v 1 -q:v 2");
                }
                else if (format == "WEBP")
                {
                    ConvertImage(ffmpegPath, inputFile, ".webp", "-frames:v 1 -c:v libwebp -quality 90");
                }
                else if (format == "BMP")
                {
                    ConvertImage(ffmpegPath, inputFile, ".bmp", "-frames:v 1");
                }
                else if (format == "TIFF")
                {
                    ConvertImage(ffmpegPath, inputFile, ".tiff", "-frames:v 1");
                }
                else if (format == "GIF")
                {
                    ConvertToAnimatedGif(ffmpegPath, inputFile);
                }
                return;
            }

            if (format == "MP4")
            {
                ConvertToMp4(ffmpegPath, inputFile);
            }
            else if (format == "MKV")
            {
                ConvertToContainer(ffmpegPath, inputFile, ".mkv");
            }
            else if (format == "AVI")
            {
                ConvertToContainer(ffmpegPath, inputFile, ".avi");
            }
            else if (format == "MOV")
            {
                ConvertToVideo(ffmpegPath, inputFile, ".mov");
            }
            else if (format == "WEBM")
            {
                ConvertToVideo(ffmpegPath, inputFile, ".webm");
            }
        }

        private void ConvertToAnimatedGif(string ffmpegPath, string inputFile)
        {
            string outputFile = GetOutputPath(inputFile, ".gif", "converted");
            if (string.Equals(inputFile, outputFile, StringComparison.OrdinalIgnoreCase))
            {
                Log("File is already GIF: " + Path.GetFileName(inputFile));
                return;
            }

            string filter = "\"[0:v]fps=12,scale=640:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\"";
            DeleteIfExists(outputFile);
            if (!RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " -filter_complex " + filter + " -loop 0 " + Quote(outputFile)))
            {
                DeleteIfExists(outputFile);
            }
            else
            {
                CompleteConvertedOutput(inputFile, outputFile);
            }
        }

        private void ConvertImage(string ffmpegPath, string inputFile, string extension, string arguments)
        {
            string outputFile = GetOutputPath(inputFile, extension, "converted");
            if (string.Equals(inputFile, outputFile, StringComparison.OrdinalIgnoreCase))
            {
                Log("File is already " + extension.ToUpperInvariant().TrimStart('.') + ": " + Path.GetFileName(inputFile));
                return;
            }

            DeleteIfExists(outputFile);
            if (!RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " " + arguments + " " + Quote(outputFile)))
            {
                DeleteIfExists(outputFile);
            }
            else
            {
                CompleteConvertedOutput(inputFile, outputFile);
            }
        }

        private void ConvertToContainer(string ffmpegPath, string inputFile, string extension)
        {
            string outputFile = GetOutputPath(inputFile, extension, "converted");
            if (string.Equals(inputFile, outputFile, StringComparison.OrdinalIgnoreCase))
            {
                Log("File is already " + extension.ToUpperInvariant().TrimStart('.') + ": " + Path.GetFileName(inputFile));
                return;
            }

            DeleteIfExists(outputFile);
            if (!RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " -c copy " + Quote(outputFile)))
            {
                DeleteIfExists(outputFile);
            }
            else
            {
                CompleteConvertedOutput(inputFile, outputFile);
            }
        }

        private void ConvertAudio(string ffmpegPath, string inputFile, string extension, string arguments)
        {
            string outputFile = GetOutputPath(inputFile, extension, "converted");
            if (string.Equals(inputFile, outputFile, StringComparison.OrdinalIgnoreCase))
            {
                Log("File is already " + extension.ToUpperInvariant().TrimStart('.') + ": " + Path.GetFileName(inputFile));
                return;
            }

            DeleteIfExists(outputFile);
            if (!RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " " + arguments + " " + Quote(outputFile)))
            {
                DeleteIfExists(outputFile);
            }
            else
            {
                CompleteConvertedOutput(inputFile, outputFile);
            }
        }

        private void ConvertToVideo(string ffmpegPath, string inputFile, string extension)
        {
            string outputFile = GetOutputPath(inputFile, extension, "converted");
            if (string.Equals(inputFile, outputFile, StringComparison.OrdinalIgnoreCase))
            {
                Log("File is already " + extension.ToUpperInvariant().TrimStart('.') + ": " + Path.GetFileName(inputFile));
                return;
            }

            string arguments = "-c:v libx264 -preset slow -crf 18 -c:a aac -b:a 192k";
            if (extension == ".webm")
            {
                arguments = "-c:v libvpx-vp9 -b:v 0 -crf 32 -c:a libopus";
            }

            DeleteIfExists(outputFile);
            if (!RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " " + arguments + " " + Quote(outputFile)))
            {
                DeleteIfExists(outputFile);
            }
            else
            {
                CompleteConvertedOutput(inputFile, outputFile);
            }
        }

        private void RemuxRepair(string ffmpegPath, string inputFile)
        {
            string extension = Path.GetExtension(inputFile);
            bool replaceOriginal = ShouldReplaceOriginal();
            string tempFile = replaceOriginal
                ? Path.Combine(Path.GetDirectoryName(inputFile), Path.GetFileNameWithoutExtension(inputFile) + "_remux_temp" + extension)
                : GetUniquePath(Path.Combine(Path.GetDirectoryName(inputFile), Path.GetFileNameWithoutExtension(inputFile) + "_remux" + extension));
            string arguments = "-map 0 -c copy -map_metadata 0";
            if (SupportsFastStart(extension))
            {
                arguments += " -movflags +faststart";
            }

            DeleteIfExists(tempFile);
            if (RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " " + arguments + " " + Quote(tempFile)))
            {
                if (replaceOriginal)
                {
                    ReplaceFile(tempFile, inputFile);
                }
            }
            else
            {
                DeleteIfExists(tempFile);
            }
        }

        private bool SupportsFastStart(string extension)
        {
            return string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".mov", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase);
        }

        private void ConvertToMp4(string ffmpegPath, string inputFile)
        {
            if (HasExtension(inputFile, ".mkv"))
            {
                ConvertThenRemuxAndDeleteOriginal(ffmpegPath, inputFile, ".mkv", ".mp4", "-map 0 -map -0:t? -map -0:d? -c:v copy -c:a copy -c:s mov_text -movflags +faststart");
            }
            else if (HasExtension(inputFile, ".avi"))
            {
                ConvertThenRemuxAndDeleteOriginal(ffmpegPath, inputFile, ".avi", ".mp4", "-map 0 -map -0:t? -map -0:d? -c:v libx264 -preset slow -crf 18 -c:a aac -b:a 192k -c:s mov_text -movflags +faststart");
            }
            else
            {
                ConvertToMp4Generic(ffmpegPath, inputFile);
            }
        }

        private void ConvertToMp4Generic(string ffmpegPath, string inputFile)
        {
            string outputFile = GetOutputPath(inputFile, ".mp4", "converted");
            string remuxFile = Path.Combine(Path.GetDirectoryName(outputFile), Path.GetFileNameWithoutExtension(outputFile) + "_remux.mp4");

            if (string.Equals(inputFile, outputFile, StringComparison.OrdinalIgnoreCase))
            {
                Log("File is already MP4: " + Path.GetFileName(inputFile));
                return;
            }

            DeleteIfExists(outputFile);
            DeleteIfExists(remuxFile);

            if (!RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " -map 0 -map -0:t? -map -0:d? -c:v libx264 -preset slow -crf 18 -c:a aac -b:a 192k -c:s mov_text -movflags +faststart " + Quote(outputFile)))
            {
                DeleteIfExists(outputFile);
                return;
            }

            if (RunFFmpeg(ffmpegPath, "-i " + Quote(outputFile) + " -map 0 -c copy " + Quote(remuxFile)))
            {
                DeleteIfExists(outputFile);
                File.Move(remuxFile, outputFile);
                CompleteConvertedOutput(inputFile, outputFile);
            }
            else
            {
                DeleteIfExists(remuxFile);
            }
        }

        private void ReplaceWithTranscode(string ffmpegPath, string inputFile, string tempSuffix, string arguments, string remuxSuffix)
        {
            bool replaceOriginal = ShouldReplaceOriginal();
            string finalExtension = Path.GetExtension(tempSuffix);
            string tempFile = replaceOriginal
                ? Path.Combine(Path.GetDirectoryName(inputFile), Path.GetFileNameWithoutExtension(inputFile) + tempSuffix)
                : GetOutputPath(inputFile, finalExtension, "processed");
            DeleteIfExists(tempFile);

            if (!RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " " + arguments + " " + Quote(tempFile)))
            {
                DeleteIfExists(tempFile);
                return;
            }

            string currentOutput = tempFile;

            if (!string.IsNullOrEmpty(remuxSuffix))
            {
                string remuxFile = replaceOriginal
                    ? Path.Combine(Path.GetDirectoryName(inputFile), Path.GetFileNameWithoutExtension(inputFile) + remuxSuffix)
                    : GetUniquePath(Path.Combine(Path.GetDirectoryName(currentOutput), Path.GetFileNameWithoutExtension(currentOutput) + "_remux" + Path.GetExtension(currentOutput)));
                DeleteIfExists(remuxFile);
                if (RunFFmpeg(ffmpegPath, "-i " + Quote(currentOutput) + " -c copy " + Quote(remuxFile)))
                {
                    DeleteIfExists(currentOutput);
                    currentOutput = remuxFile;
                }
                else
                {
                    DeleteIfExists(remuxFile);
                }
            }

            if (replaceOriginal)
            {
                ReplaceFile(currentOutput, inputFile);
            }
        }

        private void ConvertThenRemuxAndDeleteOriginal(string ffmpegPath, string inputFile, string expectedExtension, string outputExtension, string arguments)
        {
            if (!HasExtension(inputFile, expectedExtension))
            {
                Log("Expected extension " + expectedExtension + ": " + Path.GetFileName(inputFile));
                return;
            }

            string outputFile = GetOutputPath(inputFile, outputExtension, "converted");
            string remuxFile = Path.Combine(Path.GetDirectoryName(outputFile), Path.GetFileNameWithoutExtension(outputFile) + "_remux" + outputExtension);

            DeleteIfExists(outputFile);
            DeleteIfExists(remuxFile);

            if (!RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " " + arguments + " " + Quote(outputFile)))
            {
                DeleteIfExists(outputFile);
                return;
            }

            if (RunFFmpeg(ffmpegPath, "-i " + Quote(outputFile) + " -map 0 -c copy " + Quote(remuxFile)))
            {
                DeleteIfExists(outputFile);
                File.Move(remuxFile, outputFile);
                CompleteConvertedOutput(inputFile, outputFile);
            }
            else
            {
                DeleteIfExists(remuxFile);
            }
        }

        private void ConvertAndDeleteOriginal(string ffmpegPath, string inputFile, string expectedExtension, string outputExtension, string arguments)
        {
            if (!HasExtension(inputFile, expectedExtension))
            {
                Log("Expected extension " + expectedExtension + ": " + Path.GetFileName(inputFile));
                return;
            }

            string outputFile = GetOutputPath(inputFile, outputExtension, "converted");
            DeleteIfExists(outputFile);

            if (RunFFmpeg(ffmpegPath, "-i " + Quote(inputFile) + " " + arguments + " " + Quote(outputFile)))
            {
                CompleteConvertedOutput(inputFile, outputFile);
            }
            else
            {
                DeleteIfExists(outputFile);
            }
        }

        private void CutFile(string ffmpegPath, string inputFile, bool audioOnly)
        {
            if (audioOnly && !IsSupportedAudio(inputFile))
            {
                Log("Unsupported audio format: " + Path.GetFileName(inputFile));
                return;
            }

            TimeSpan start;
            TimeSpan end;
            if (!TryReadCutTimes(out start, out end))
            {
                return;
            }

            string outputFile = GetUniqueCutOutput(inputFile);
            string args = "-i " + Quote(inputFile)
                + " -ss " + FormatTime(start)
                + " -to " + FormatTime(end)
                + " " + GetCutArguments(inputFile, audioOnly)
                + " " + Quote(outputFile);
            if (!RunFFmpeg(ffmpegPath, args))
            {
                DeleteIfExists(outputFile);
            }
        }

        private string GetCutArguments(string inputFile, bool audioOnly)
        {
            if (audioOnly)
            {
                if (HasExtension(inputFile, ".wav"))
                {
                    return "-vn -c:a pcm_s16le";
                }

                if (HasExtension(inputFile, ".flac"))
                {
                    return "-vn -c:a flac";
                }

                if (HasExtension(inputFile, ".opus"))
                {
                    return "-vn -c:a libopus";
                }

                if (HasExtension(inputFile, ".ogg"))
                {
                    return "-vn -c:a libvorbis";
                }

                if (HasExtension(inputFile, ".m4a"))
                {
                    return "-vn -c:a aac -b:a 192k";
                }

                return "-vn -c:a libmp3lame";
            }

            if (HasExtension(inputFile, ".webm"))
            {
                return "-map 0 -map -0:t? -map -0:d? -c:v libvpx-vp9 -b:v 0 -crf 32 -c:a libopus -c:s copy";
            }

            if (HasExtension(inputFile, ".mp4") || HasExtension(inputFile, ".mov"))
            {
                return "-map 0 -map -0:t? -map -0:d? -c:v libx264 -preset veryfast -crf 18 -c:a aac -b:a 192k -c:s mov_text -movflags +faststart";
            }

            return "-map 0 -map -0:t? -map -0:d? -c:v libx264 -preset veryfast -crf 18 -c:a aac -b:a 192k -c:s copy";
        }

        private void ProcessConcat(string ffmpegPath, string[] items)
        {
            if (items.Length < 2)
            {
                Log("Merge needs at least two video files.");
                return;
            }

            string extension = Path.GetExtension(items[0]);
            if (!IsSupportedMergeVideo(items[0]))
            {
                Log("Merge accepts MP4, MKV, AVI, MOV, and WEBM files only: " + Path.GetFileName(items[0]));
                return;
            }

            for (int i = 0; i < items.Length; i++)
            {
                if (!IsSupportedMergeVideo(items[i]))
                {
                    Log("Merge accepts MP4, MKV, AVI, MOV, and WEBM files only: " + Path.GetFileName(items[i]));
                    return;
                }

                if (!string.Equals(Path.GetExtension(items[i]), extension, StringComparison.OrdinalIgnoreCase))
                {
                    Log("Merge needs files with the same format. Found " + Path.GetExtension(items[i]).ToUpperInvariant() + " after " + extension.ToUpperInvariant() + ".");
                    return;
                }
            }

            string outputDirectory = Path.GetDirectoryName(items[0]);
            string feedList = Path.Combine(Path.GetTempPath(), "ffmpeg-concat-" + Guid.NewGuid().ToString("N") + ".txt");
            string outputFile = GetUniquePath(Path.Combine(outputDirectory, "merged" + extension.ToLowerInvariant()));

            try
            {
                using (StreamWriter writer = new StreamWriter(feedList, false, new UTF8Encoding(false)))
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        writer.WriteLine("file '" + items[i].Replace("'", "'\\''") + "'");
                    }
                }

                RunFFmpeg(ffmpegPath, "-safe 0 -f concat -i " + Quote(feedList) + " -c copy " + Quote(outputFile));
            }
            finally
            {
                DeleteIfExists(feedList);
            }
        }

        private bool IsSupportedMergeVideo(string inputFile)
        {
            return HasExtension(inputFile, ".mp4")
                || HasExtension(inputFile, ".mkv")
                || HasExtension(inputFile, ".avi")
                || HasExtension(inputFile, ".mov")
                || HasExtension(inputFile, ".webm");
        }

        private string EnsureFFmpegExists()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string localFfmpeg = Path.Combine(exeDir, "ffmpeg.exe");

            string pathFfmpeg = FindFfmpegInPath();
            if (!string.IsNullOrEmpty(pathFfmpeg))
            {
                Log("ffmpeg found in PATH: " + pathFfmpeg);
                return pathFfmpeg;
            }

            if (File.Exists(localFfmpeg))
            {
                Log("ffmpeg found beside the application executable: " + localFfmpeg);
                return localFfmpeg;
            }

            string tempZip = null;
            string tempExtractDir = null;
            try
            {
                tempZip = Path.Combine(Path.GetTempPath(), "ffmpeg-download-" + Guid.NewGuid().ToString("N") + ".zip");
                tempExtractDir = Path.Combine(Path.GetTempPath(), "ffmpeg-extract-" + Guid.NewGuid().ToString("N"));

                Log("Downloading FFmpeg from BtbN: " + BtbNFfmpegZipUrl);
                using (WebClient client = new WebClient())
                {
                    client.DownloadFile(BtbNFfmpegZipUrl, tempZip);
                }

                Directory.CreateDirectory(tempExtractDir);
                ZipFile.ExtractToDirectory(tempZip, tempExtractDir);

                string extractedFfmpeg = FindFirstFile(tempExtractDir, "ffmpeg.exe");
                if (string.IsNullOrEmpty(extractedFfmpeg))
                {
                    Log("Could not find bin\\ffmpeg.exe inside the downloaded ZIP.");
                    return null;
                }

                File.Copy(extractedFfmpeg, localFfmpeg, true);
                Log("ffmpeg installed beside the application executable: " + localFfmpeg);
                return localFfmpeg;
            }
            catch (Exception ex)
            {
                Log("Error while downloading or extracting FFmpeg: " + ex.Message);
                return null;
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(tempZip))
                    {
                        DeleteIfExists(tempZip);
                    }

                    if (!string.IsNullOrEmpty(tempExtractDir) && Directory.Exists(tempExtractDir))
                    {
                        Directory.Delete(tempExtractDir, true);
                    }
                }
                catch
                {
                }
            }
        }

        private string FindFfmpegInPath()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c where ffmpeg");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode == 0 && !IsNullOrWhiteSpace(output))
                    {
                        return output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private bool RunFFmpeg(string ffmpegPath, string arguments)
        {
            Log("ffmpeg " + arguments);

            ProcessStartInfo psi = new ProcessStartInfo(ffmpegPath, arguments);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using (Process process = new Process())
            {
                bool stoppedByCancel = false;
                process.StartInfo = psi;

                process.Start();
                lock (processLock)
                {
                    currentProcess = process;
                }

                Thread outputThread = StartStreamLogThread(process.StandardOutput);
                Thread errorThread = StartStreamLogThread(process.StandardError);

                try
                {
                    while (!process.WaitForExit(250))
                    {
                        if (cancelRequested)
                        {
                            stoppedByCancel = true;
                            TryKillProcess(process);
                            Log("FFmpeg process stopped.");
                            break;
                        }
                    }

                    process.WaitForExit();
                    outputThread.Join(1000);
                    errorThread.Join(1000);
                }
                finally
                {
                    lock (processLock)
                    {
                        if (ReferenceEquals(currentProcess, process))
                        {
                            currentProcess = null;
                        }
                    }
                }

                if (stoppedByCancel)
                {
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    Log("FFmpeg failed with exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
                    return false;
                }
            }

            return true;
        }

        private bool TryReadCutTimes(out TimeSpan start, out TimeSpan end)
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;
            string startText = null;
            string endText = null;
            Dispatcher.Invoke(new Action(delegate
            {
                startText = StartTextBox.Text;
                endText = EndTextBox.Text;
            }));

            if (!TryParseTime(startText, out start) || !TryParseTime(endText, out end))
            {
                Log("Enter Start and End using HH:MM:SS.mmm. Leading zeros are optional.");
                return false;
            }

            if (end <= start)
            {
                Log("Invalid range. End time must be greater than start time.");
                return false;
            }

            return true;
        }

        private bool TryParseTime(string text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (IsNullOrWhiteSpace(text))
            {
                return false;
            }

            Match match = Regex.Match(text.Trim(), @"^(\d+)[:-](\d{1,2})[:-](\d{1,2})(?:[.,](\d{1,3}))?$");
            if (!match.Success)
            {
                return false;
            }

            int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (minutes > 59 || seconds > 59)
            {
                return false;
            }

            int milliseconds = 0;
            if (match.Groups[4].Success)
            {
                string ms = match.Groups[4].Value.PadRight(3, '0');
                milliseconds = int.Parse(ms, CultureInfo.InvariantCulture);
            }

            time = new TimeSpan(0, hours, minutes, seconds, milliseconds);
            return true;
        }

        private string GetUniqueCutOutput(string inputFile)
        {
            string directory = Path.GetDirectoryName(inputFile);
            string name = Path.GetFileNameWithoutExtension(inputFile);
            string extension = Path.GetExtension(inputFile);
            string cleanName = Regex.Replace(name, @"-cut(-\d+)?$", string.Empty, RegexOptions.IgnoreCase);
            return GetUniquePath(Path.Combine(directory, cleanName + "-cut" + extension));
        }

        private string GetUniquePath(string preferredPath)
        {
            if (!File.Exists(preferredPath))
            {
                return preferredPath;
            }

            string directory = Path.GetDirectoryName(preferredPath);
            string name = Path.GetFileNameWithoutExtension(preferredPath);
            string extension = Path.GetExtension(preferredPath);
            int index = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(directory, name + "-" + index.ToString(CultureInfo.InvariantCulture) + extension);
                index++;
            }
            while (File.Exists(candidate));

            return candidate;
        }

        private void ReplaceFile(string sourceFile, string targetFile)
        {
            DeleteIfExists(targetFile);
            File.Move(sourceFile, targetFile);
        }

        private void CompleteConvertedOutput(string inputFile, string outputFile)
        {
            if (!ShouldReplaceOriginal())
            {
                return;
            }

            if (string.Equals(Path.GetExtension(inputFile), Path.GetExtension(outputFile), StringComparison.OrdinalIgnoreCase))
            {
                ReplaceFile(outputFile, inputFile);
            }
            else
            {
                DeleteIfExists(inputFile);
            }
        }

        private string GetOutputPath(string inputFile, string outputExtension, string suffix)
        {
            string directory = Path.GetDirectoryName(inputFile);
            string name = Path.GetFileNameWithoutExtension(inputFile);

            if (ShouldReplaceOriginal() && !string.Equals(Path.GetExtension(inputFile), outputExtension, StringComparison.OrdinalIgnoreCase))
            {
                return GetUniquePath(Path.Combine(directory, name + outputExtension));
            }

            if (ShouldReplaceOriginal())
            {
                return Path.Combine(directory, name + "_" + suffix + "_temp" + outputExtension);
            }

            return GetUniquePath(Path.Combine(directory, name + "_" + suffix + outputExtension));
        }

        private bool ShouldReplaceOriginal()
        {
            bool replace = false;
            Dispatcher.Invoke(new Action(delegate
            {
                replace = ReplaceOriginalCheckBox.IsChecked == true;
            }));

            return replace;
        }

        private void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private string FindFirstFile(string rootPath, string fileName)
        {
            string[] files = Directory.GetFiles(rootPath, fileName, SearchOption.AllDirectories);
            return files.Length == 0 ? null : files[0];
        }

        private bool IsSupportedAudio(string inputFile)
        {
            string extension = Path.GetExtension(inputFile);
            return HasExtension(extension, ".mp3")
                || HasExtension(extension, ".m4a")
                || HasExtension(extension, ".wav")
                || HasExtension(extension, ".flac")
                || HasExtension(extension, ".ogg")
                || HasExtension(extension, ".opus");
        }

        private bool HasExtension(string inputFile, string extension)
        {
            return string.Equals(Path.GetExtension(inputFile), extension, StringComparison.OrdinalIgnoreCase)
                || string.Equals(inputFile, extension, StringComparison.OrdinalIgnoreCase);
        }

        private string[] GetQueuedFiles()
        {
            List<string> files = new List<string>();
            for (int i = 0; i < FilesList.Items.Count; i++)
            {
                string item = FilesList.Items[i] as string;
                if (!string.IsNullOrEmpty(item))
                {
                    files.Add(item);
                }
            }

            return files.ToArray();
        }

        private string GetSelectedTool()
        {
            ComboBoxItem selectedItem = ToolSelector.SelectedItem as ComboBoxItem;
            return selectedItem == null ? string.Empty : selectedItem.Content as string;
        }

        private string GetSelectedFormat()
        {
            string value = null;
            Dispatcher.Invoke(new Action(delegate
            {
                ComboBoxItem selectedItem = FormatSelector.SelectedItem as ComboBoxItem;
                value = selectedItem == null ? "MP4" : selectedItem.Content as string;
            }));

            return value;
        }

        private bool IsConvertAudioSelected()
        {
            bool audio = false;
            Dispatcher.Invoke(new Action(delegate
            {
                ComboBoxItem selectedItem = ConvertTypeSelector.SelectedItem as ComboBoxItem;
                audio = selectedItem != null && string.Equals(selectedItem.Content as string, "Audio", StringComparison.OrdinalIgnoreCase);
            }));

            return audio;
        }

        private bool IsConvertImageSelected()
        {
            bool image = false;
            Dispatcher.Invoke(new Action(delegate
            {
                ComboBoxItem selectedItem = ConvertTypeSelector.SelectedItem as ComboBoxItem;
                image = selectedItem != null && string.Equals(selectedItem.Content as string, "Image", StringComparison.OrdinalIgnoreCase);
            }));

            return image;
        }

        private void RefreshConvertFormats()
        {
            if (FormatSelector == null || ConvertTypeSelector == null)
            {
                return;
            }

            ComboBoxItem selectedType = ConvertTypeSelector.SelectedItem as ComboBoxItem;
            string type = selectedType == null ? "Video" : selectedType.Content as string;
            FormatSelector.Items.Clear();

            if (string.Equals(type, "Audio", StringComparison.OrdinalIgnoreCase))
            {
                AddFormatItem("MP3");
                AddFormatItem("WAV");
                AddFormatItem("OGG");
                AddFormatItem("FLAC");
                AddFormatItem("M4A");
                AddFormatItem("OPUS");
            }
            else if (string.Equals(type, "Image", StringComparison.OrdinalIgnoreCase))
            {
                AddFormatItem("PNG");
                AddFormatItem("JPG");
                AddFormatItem("WEBP");
                AddFormatItem("BMP");
                AddFormatItem("TIFF");
                AddFormatItem("GIF");
            }
            else
            {
                AddFormatItem("MP4");
                AddFormatItem("MKV");
                AddFormatItem("AVI");
                AddFormatItem("MOV");
                AddFormatItem("WEBM");
            }

            FormatSelector.SelectedIndex = 0;
            UpdateGifOptionsVisibility();
        }

        private void UpdateGifOptionsVisibility()
        {
            if (GifParams == null)
            {
                return;
            }

            bool showGifParams = string.Equals(GetSelectedTool(), "Convert", StringComparison.OrdinalIgnoreCase)
                && IsConvertImageSelected()
                && string.Equals(GetSelectedFormat(), "GIF", StringComparison.OrdinalIgnoreCase);

            GifParams.Visibility = showGifParams ? Visibility.Visible : Visibility.Collapsed;
        }

        private int GetGifFps()
        {
            int fps = 12;
            Dispatcher.Invoke(new Action(delegate
            {
                ComboBoxItem selectedItem = GifFpsSelector.SelectedItem as ComboBoxItem;
                if (selectedItem != null)
                {
                    int parsed;
                    if (int.TryParse(selectedItem.Content as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        fps = parsed;
                    }
                }
            }));

            return fps;
        }

        private int GetGifWidth()
        {
            string quality = "Medium";
            Dispatcher.Invoke(new Action(delegate
            {
                ComboBoxItem selectedItem = GifQualitySelector.SelectedItem as ComboBoxItem;
                if (selectedItem != null && selectedItem.Content != null)
                {
                    quality = selectedItem.Content as string;
                }
            }));

            if (string.Equals(quality, "Low", StringComparison.OrdinalIgnoreCase))
            {
                return 480;
            }

            if (string.Equals(quality, "High", StringComparison.OrdinalIgnoreCase))
            {
                return 800;
            }

            return 640;
        }

        private void AddFormatItem(string format)
        {
            ComboBoxItem item = new ComboBoxItem();
            item.Content = format;
            FormatSelector.Items.Add(item);
        }

        private bool IsCutAudioSelected()
        {
            bool audio = false;
            Dispatcher.Invoke(new Action(delegate
            {
                ComboBoxItem selectedItem = CutTypeSelector.SelectedItem as ComboBoxItem;
                audio = selectedItem != null && string.Equals(selectedItem.Content as string, "Audio", StringComparison.OrdinalIgnoreCase);
            }));

            return audio;
        }

        private int GetCompressionCrf()
        {
            int quality = 100;
            Dispatcher.Invoke(new Action(delegate
            {
                ComboBoxItem selectedItem = QualitySelector.SelectedItem as ComboBoxItem;
                if (selectedItem != null)
                {
                    string text = (selectedItem.Content as string).Replace("%", string.Empty);
                    int parsed;
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        quality = parsed;
                    }
                }
            }));

            if (quality >= 100)
            {
                return 28;
            }

            if (quality <= 10)
            {
                return 42;
            }

            return 28 + ((100 - quality) * 14 / 90);
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }

        private static string FormatTime(TimeSpan value)
        {
            return ((int)value.TotalHours).ToString("00", CultureInfo.InvariantCulture)
                + ":" + value.Minutes.ToString("00", CultureInfo.InvariantCulture)
                + ":" + value.Seconds.ToString("00", CultureInfo.InvariantCulture)
                + "." + value.Milliseconds.ToString("000", CultureInfo.InvariantCulture);
        }

        private Thread StartStreamLogThread(StreamReader reader)
        {
            Thread thread = new Thread(delegate()
            {
                PumpStreamToLog(reader);
            });
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        private void PumpStreamToLog(StreamReader reader)
        {
            StringBuilder buffer = new StringBuilder();
            DateTime lastCarriageLog = DateTime.MinValue;

            try
            {
                int value;
                while ((value = reader.Read()) != -1)
                {
                    char ch = (char)value;
                    if (ch == '\n')
                    {
                        FlushStreamBuffer(buffer);
                    }
                    else if (ch == '\r')
                    {
                        if (buffer.Length > 0 && (DateTime.UtcNow - lastCarriageLog).TotalMilliseconds >= 650)
                        {
                            FlushStreamBuffer(buffer);
                            lastCarriageLog = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        buffer.Append(ch);
                    }
                }

                FlushStreamBuffer(buffer);
            }
            catch
            {
            }
        }

        private void FlushStreamBuffer(StringBuilder buffer)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            string text = buffer.ToString().Trim();
            buffer.Length = 0;
            if (!IsNullOrWhiteSpace(text))
            {
                Log(text);
            }
        }

        private void Log(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(Log), text);
                return;
            }

            LogTextBox.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " - " + text + Environment.NewLine);
            if (AutoScrollCheckBox.IsChecked == true)
            {
                LogTextBox.ScrollToLine(LogTextBox.LineCount - 1);
                LogTextBox.ScrollToHorizontalOffset(0);
            }
        }

        private void ShowAboutWindow()
        {
            Window about = new Window();
            about.Title = "About";
            about.Owner = this;
            about.Width = 360;
            about.Height = 372;
            about.ResizeMode = ResizeMode.NoResize;
            about.WindowStyle = WindowStyle.None;
            about.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            about.Background = new SolidColorBrush(GetThemeColor("Window"));
            about.Foreground = new SolidColorBrush(GetThemeColor("Text"));
            about.Icon = new BitmapImage(new Uri("pack://application:,,,/icon.ico", UriKind.Absolute));

            Border shell = new Border();
            shell.Background = new SolidColorBrush(GetThemeColor("Window"));
            shell.BorderBrush = new SolidColorBrush(GetThemeColor("Border"));
            shell.BorderThickness = new Thickness(1);

            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(22);
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;

            Image logo = new Image();
            logo.Source = new BitmapImage(new Uri("pack://application:,,,/logo.png", UriKind.Absolute));
            logo.Width = 96;
            logo.Height = 96;
            logo.Margin = new Thickness(0, 8, 0, 18);
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(logo);

            TextBlock title = new TextBlock();
            title.Text = "FFmpeg Converter Tools GUI";
            title.FontSize = 18;
            title.FontWeight = FontWeights.Bold;
            title.TextAlignment = TextAlignment.Center;
            title.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(title);

            TextBlock version = new TextBlock();
            version.Text = "Version " + GetCurrentAppVersionText();
            version.TextAlignment = TextAlignment.Center;
            version.Foreground = new SolidColorBrush(GetThemeColor("MutedText"));
            version.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(version);

            TextBlock credits = new TextBlock();
            credits.Text = "Created by R4wd0G\n\nA practical FFmpeg batch toolkit reimagined as a native Windows GUI for faster everyday media conversion.";
            credits.TextAlignment = TextAlignment.Center;
            credits.TextWrapping = TextWrapping.Wrap;
            credits.Foreground = new SolidColorBrush(GetThemeColor("MutedText"));
            credits.Margin = new Thickness(0, 0, 0, 16);
            panel.Children.Add(credits);

            StackPanel buttonRow = new StackPanel();
            buttonRow.Orientation = Orientation.Horizontal;
            buttonRow.HorizontalAlignment = HorizontalAlignment.Center;
            buttonRow.Margin = new Thickness(0);

            Border closeButton = new Border();
            closeButton.Width = 96;
            closeButton.Height = 34;
            closeButton.CornerRadius = new CornerRadius(8);
            closeButton.Background = new SolidColorBrush(GetThemeColor("Accent"));
            closeButton.BorderBrush = new SolidColorBrush(GetThemeColor("AccentHover"));
            closeButton.BorderThickness = new Thickness(1);
            closeButton.HorizontalAlignment = HorizontalAlignment.Center;
            closeButton.Cursor = Cursors.Hand;
            closeButton.MouseEnter += delegate { closeButton.Background = new SolidColorBrush(GetThemeColor("AccentHover")); };
            closeButton.MouseLeave += delegate { closeButton.Background = new SolidColorBrush(GetThemeColor("Accent")); };

            TextBlock closeText = new TextBlock();
            closeText.Text = "Close";
            closeText.Foreground = new SolidColorBrush(GetThemeColor("AccentText"));
            closeText.FontWeight = FontWeights.Bold;
            closeText.HorizontalAlignment = HorizontalAlignment.Center;
            closeText.VerticalAlignment = VerticalAlignment.Center;
            closeButton.Child = closeText;
            buttonRow.Children.Add(closeButton);
            panel.Children.Add(buttonRow);

            shell.Child = panel;
            about.Content = shell;

            about.Opacity = 0;

            PlayCreditsSound();

            bool closeStarted = false;
            bool allowClose = false;
            DispatcherTimer aboutFadeTimer = null;
            DateTime showFadeStart = DateTime.Now;
            DispatcherTimer aboutShowFadeTimer = new DispatcherTimer();
            aboutShowFadeTimer.Interval = TimeSpan.FromMilliseconds(16);
            aboutShowFadeTimer.Tick += delegate
            {
                double elapsed = (DateTime.Now - showFadeStart).TotalMilliseconds;
                double progress = Math.Min(1, elapsed / 400);
                about.Opacity = progress;

                if (progress >= 1)
                {
                    about.Opacity = 1;
                    aboutShowFadeTimer.Stop();
                }
            };
            aboutShowFadeTimer.Start();

            Action beginClose = delegate
            {
                if (closeStarted)
                {
                    return;
                }

                closeStarted = true;
                closeButton.IsHitTestVisible = false;
                closeButton.Opacity = 0.65;

                StartCreditsFadeOut(700);

                DateTime fadeStart = DateTime.Now;
                aboutFadeTimer = new DispatcherTimer();
                aboutFadeTimer.Interval = TimeSpan.FromMilliseconds(16);
                aboutFadeTimer.Tick += delegate
                {
                    double elapsed = (DateTime.Now - fadeStart).TotalMilliseconds;
                    double progress = Math.Min(1, elapsed / 700);
                    about.Opacity = 1 - progress;

                    if (progress >= 1)
                    {
                        aboutFadeTimer.Stop();
                        allowClose = true;
                        about.Close();
                    }
                };
                aboutFadeTimer.Start();
            };

            closeButton.MouseLeftButtonUp += delegate { beginClose(); };
            about.Closing += delegate(object sender, CancelEventArgs e)
            {
                if (!allowClose)
                {
                    e.Cancel = true;
                    beginClose();
                }
            };
            about.Closed += delegate
            {
                aboutShowFadeTimer.Stop();

                if (aboutFadeTimer != null)
                {
                    aboutFadeTimer.Stop();
                }

                StopCreditsSound();
            };
            about.ShowDialog();
        }

        private void BeginCheckForUpdates(bool interactive, Window owner)
        {
            lock (updateLock)
            {
                if (updateCheckInProgress)
                {
                    if (interactive)
                    {
                        ShowMessage(owner, "An update check is already in progress.", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }

                    return;
                }

                updateCheckInProgress = true;
            }

            if (interactive)
            {
                Log("Checking for updates...");
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    CheckForUpdatesCore(interactive, owner);
                }
                finally
                {
                    lock (updateLock)
                    {
                        updateCheckInProgress = false;
                    }
                }
            });
        }

        private void CheckForUpdatesCore(bool interactive, Window owner)
        {
            try
            {
                GitHubReleaseInfo release = GetLatestReleaseInfo();
                if (release == null)
                {
                    if (interactive)
                    {
                        ShowMessage(owner, "Could not retrieve the latest release information from GitHub.", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    return;
                }

                Version currentVersion;
                Version latestVersion;
                string currentVersionText = GetCurrentAppVersionText();
                string latestVersionText = NormalizeVersionText(release.TagName);

                if (!TryParseVersion(currentVersionText, out currentVersion) || !TryParseVersion(latestVersionText, out latestVersion))
                {
                    if (interactive)
                    {
                        Log("Update check failed: invalid version information.");
                        ShowMessage(owner, "Could not compare the current version with the latest release.", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    return;
                }

                if (latestVersion.CompareTo(currentVersion) <= 0)
                {
                    if (interactive)
                    {
                        Log("No updates found.");
                        ShowMessage(owner, "You are already using the latest version (" + currentVersionText + ").", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }

                    return;
                }

                ReleaseAssetInfo asset = FindBestUpdateAsset(release, DetectInstallMode());
                if (asset == null)
                {
                    if (interactive)
                    {
                        Log("Update found, but no compatible asset is available in the latest release.");
                        ShowMessage(owner, "A newer release was found, but no compatible download asset is available.", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    return;
                }

                MessageBoxResult result = ShowMessage(
                    owner,
                    "New update found!\n\nCurrent: " + currentVersionText + "\nNew: " + latestVersionText + "\n\nDownload and install?",
                    "Update available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result != MessageBoxResult.Yes)
                {
                    if (interactive)
                    {
                        Log("Update prompt dismissed by the user.");
                    }

                    return;
                }

                if (IsInstallerAsset(asset.Name))
                {
                    DownloadAndLaunchInstallerUpdate(asset, owner);
                }
                else
                {
                    DownloadAndLaunchPortableUpdate(asset, owner);
                }
            }
            catch (Exception ex)
            {
                if (interactive)
                {
                    Log("Update check failed: " + ex.Message);
                    ShowMessage(owner, "Update check failed: " + ex.Message, "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private GitHubReleaseInfo GetLatestReleaseInfo()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(GitHubLatestReleaseApiUrl);
            request.Accept = "application/vnd.github+json";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.UserAgent = GitHubApiUserAgent;

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream responseStream = response.GetResponseStream())
            {
                if (responseStream == null)
                {
                    return null;
                }

                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(GitHubReleaseInfo));
                return serializer.ReadObject(responseStream) as GitHubReleaseInfo;
            }
        }

        private ReleaseAssetInfo FindBestUpdateAsset(GitHubReleaseInfo release, InstallMode installMode)
        {
            ReleaseAssetInfo preferred = null;
            ReleaseAssetInfo fallback = null;
            if (release == null || release.Assets == null)
            {
                return null;
            }

            for (int i = 0; i < release.Assets.Length; i++)
            {
                ReleaseAssetInfo asset = release.Assets[i];
                if (asset == null || IsNullOrWhiteSpace(asset.Name) || IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                {
                    continue;
                }

                if (installMode == InstallMode.Installer && IsInstallerAsset(asset.Name))
                {
                    return asset;
                }

                if (installMode == InstallMode.Portable && IsPortableAsset(asset.Name))
                {
                    return asset;
                }

                if (fallback == null && (IsPortableAsset(asset.Name) || IsInstallerAsset(asset.Name)))
                {
                    fallback = asset;
                }

                if (preferred == null)
                {
                    preferred = asset;
                }
            }

            return fallback ?? preferred;
        }

        private void DownloadAndLaunchInstallerUpdate(ReleaseAssetInfo asset, Window owner)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), asset.Name);
            DeleteIfExists(tempFile);
            DownloadFile(asset.BrowserDownloadUrl, tempFile);

            Log("Installer update downloaded: " + tempFile);
            ShowMessage(owner, "The installer has been downloaded. The app will now close so the update can continue.", "Update ready", MessageBoxButton.OK, MessageBoxImage.Information);

            ProcessStartInfo startInfo = new ProcessStartInfo(tempFile, "/CURRENTUSER");
            startInfo.UseShellExecute = true;
            startInfo.WorkingDirectory = Path.GetDirectoryName(tempFile);
            Process.Start(startInfo);

            Dispatcher.BeginInvoke(new Action(delegate
            {
                Application.Current.Shutdown();
            }));
        }

        private void DownloadAndLaunchPortableUpdate(ReleaseAssetInfo asset, Window owner)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "FFmpegConverterGUI-Update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            string zipPath = Path.Combine(tempRoot, asset.Name);
            string scriptPath = Path.Combine(tempRoot, "ApplyPortableUpdate.ps1");
            string targetDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string appExePath = Assembly.GetExecutingAssembly().Location;

            DownloadFile(asset.BrowserDownloadUrl, zipPath);
            File.WriteAllText(scriptPath, BuildPortableUpdateScript(), new UTF8Encoding(false));

            Log("Portable update downloaded: " + zipPath);
            ShowMessage(owner, "The portable package has been downloaded. The app will now close so the files can be replaced.", "Update ready", MessageBoxButton.OK, MessageBoxImage.Information);

            ProcessStartInfo startInfo = new ProcessStartInfo("powershell.exe");
            startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File "
                + Quote(scriptPath)
                + " -PidToWait " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                + " -ZipPath " + Quote(zipPath)
                + " -TargetDir " + Quote(targetDirectory)
                + " -AppExe " + Quote(appExePath);
            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = false;
            startInfo.WorkingDirectory = tempRoot;
            Process.Start(startInfo);

            Dispatcher.BeginInvoke(new Action(delegate
            {
                Application.Current.Shutdown();
            }));
        }

        private void DownloadFile(string url, string destinationPath)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            using (WebClient client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = GitHubApiUserAgent;
                client.DownloadFile(url, destinationPath);
            }
        }

        private string BuildPortableUpdateScript()
        {
            return @"
param(
    [int]$PidToWait,
    [string]$ZipPath,
    [string]$TargetDir,
    [string]$AppExe
)

$ErrorActionPreference = 'Stop'
$extractDir = $null

try {
    while (Get-Process -Id $PidToWait -ErrorAction SilentlyContinue) {
        Start-Sleep -Milliseconds 500
    }

    $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ('FFmpegConverterGUI-Extract-' + [Guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $extractDir -Force

    Get-ChildItem -LiteralPath $extractDir | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $TargetDir $_.Name) -Recurse -Force
    }

    Start-Process -FilePath $AppExe
}
catch {
}
finally {
    if ($extractDir -and (Test-Path $extractDir)) {
        Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
    }
}
";
        }

        private InstallMode DetectInstallMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InstallRegistrySubkey))
                {
                    if (key != null)
                    {
                        object value = key.GetValue(InstallRegistryValueName);
                        string marker = value as string;
                        if (!string.IsNullOrWhiteSpace(marker) && string.Equals(marker.Trim(), "installer", StringComparison.OrdinalIgnoreCase))
                        {
                            return InstallMode.Installer;
                        }

                        if (!string.IsNullOrWhiteSpace(marker) && string.Equals(marker.Trim(), "portable", StringComparison.OrdinalIgnoreCase))
                        {
                            return InstallMode.Portable;
                        }
                    }
                }
            }
            catch
            {
            }

            string appDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string defaultInstalledDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FFmpeg Converter Tools GUI");

            if (appDirectory.StartsWith(defaultInstalledDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return InstallMode.Installer;
            }

            return InstallMode.Portable;
        }

        private bool IsInstallerAsset(string assetName)
        {
            return assetName.EndsWith("-installer.exe", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPortableAsset(string assetName)
        {
            return assetName.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase);
        }

        private string GetCurrentAppVersionText()
        {
            object[] attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            if (attributes != null && attributes.Length > 0)
            {
                AssemblyInformationalVersionAttribute informational = attributes[0] as AssemblyInformationalVersionAttribute;
                if (informational != null && !IsNullOrWhiteSpace(informational.InformationalVersion))
                {
                    return informational.InformationalVersion.Trim();
                }
            }

            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0" : version.ToString(3);
        }

        private string NormalizeVersionText(string versionText)
        {
            if (IsNullOrWhiteSpace(versionText))
            {
                return string.Empty;
            }

            string normalized = versionText.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(1);
            }

            int dashIndex = normalized.IndexOf('-');
            if (dashIndex > 0)
            {
                normalized = normalized.Substring(0, dashIndex);
            }

            return normalized;
        }

        private bool TryParseVersion(string versionText, out Version version)
        {
            version = null;
            string normalized = NormalizeVersionText(versionText);
            if (IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            try
            {
                version = new Version(normalized);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private MessageBoxResult ShowMessage(Window owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        {
            MessageBoxResult result = MessageBoxResult.None;

            Action showAction = delegate
            {
                Window messageOwner = owner;
                if (messageOwner == null || !messageOwner.IsLoaded)
                {
                    messageOwner = this.IsLoaded ? this : null;
                }

                if (messageOwner != null)
                {
                    result = MessageBox.Show(messageOwner, text, caption, buttons, icon);
                }
                else
                {
                    result = MessageBox.Show(text, caption, buttons, icon);
                }
            };

            if (Dispatcher.CheckAccess())
            {
                showAction();
            }
            else
            {
                Dispatcher.Invoke(showAction);
            }

            return result;
        }

        private void PlayCreditsSound()
        {
            try
            {
                StopCreditsSound();
                creditsSoundPlayer = new MediaPlayer();
                creditsLoopHandler = delegate(object sender, EventArgs e)
                {
                    if (creditsSoundPlayer != null)
                    {
                        creditsSoundPlayer.Position = TimeSpan.Zero;
                        creditsSoundPlayer.Play();
                    }
                };
                creditsSoundPlayer.MediaEnded += creditsLoopHandler;
                creditsSoundPlayer.Open(new Uri(GetExtractedResourcePath("credits.mp3"), UriKind.Absolute));
                creditsSoundPlayer.Volume = 0.55;
                creditsSoundPlayer.Play();
            }
            catch (Exception ex)
            {
                Log("Could not play credits sound: " + ex.Message);
            }
        }

        private void StartCreditsFadeOut(int durationMs)
        {
            if (creditsSoundPlayer == null)
            {
                return;
            }

            StopCreditsFadeOutTimer();

            double startVolume = creditsSoundPlayer.Volume;
            DateTime fadeStart = DateTime.Now;
            creditsFadeOutTimer = new DispatcherTimer();
            creditsFadeOutTimer.Interval = TimeSpan.FromMilliseconds(16);
            creditsFadeOutTimer.Tick += delegate
            {
                if (creditsSoundPlayer == null)
                {
                    StopCreditsFadeOutTimer();
                    return;
                }

                double elapsed = (DateTime.Now - fadeStart).TotalMilliseconds;
                double progress = Math.Min(1.0, elapsed / durationMs);
                creditsSoundPlayer.Volume = startVolume * (1.0 - progress);

                if (progress >= 1.0)
                {
                    creditsSoundPlayer.Volume = 0.0;
                    StopCreditsFadeOutTimer();
                }
            };
            creditsFadeOutTimer.Start();
        }

        private void StopCreditsFadeOutTimer()
        {
            if (creditsFadeOutTimer != null)
            {
                creditsFadeOutTimer.Stop();
                creditsFadeOutTimer = null;
            }
        }

        private void StopCreditsSound()
        {
            StopCreditsFadeOutTimer();

            if (creditsSoundPlayer != null)
            {
                try
                {
                    if (creditsLoopHandler != null)
                    {
                        creditsSoundPlayer.MediaEnded -= creditsLoopHandler;
                    }
                    creditsSoundPlayer.Stop();
                    creditsSoundPlayer.Close();
                }
                catch
                {
                }

                creditsSoundPlayer = null;
            }

            creditsLoopHandler = null;
        }

        private void PlayCompletionSound()
        {
            try
            {
                if (completionSoundPlayer != null)
                {
                    completionSoundPlayer.Stop();
                    completionSoundPlayer.Close();
                }

                completionSoundPlayer = new MediaPlayer();
                completionSoundPlayer.Open(new Uri(GetExtractedResourcePath("completed.mp3"), UriKind.Absolute));
                completionSoundPlayer.Play();
            }
            catch (Exception ex)
            {
                Log("Could not play completion sound: " + ex.Message);
            }
        }

        private string GetExtractedResourcePath(string fileName)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "FFmpegConverterGUI-" + fileName);
            if (File.Exists(tempPath))
            {
                return tempPath;
            }

            Uri resourceUri = new Uri("pack://application:,,,/" + fileName, UriKind.Absolute);
            System.Windows.Resources.StreamResourceInfo resourceInfo = Application.GetResourceStream(resourceUri);
            if (resourceInfo == null || resourceInfo.Stream == null)
            {
                throw new FileNotFoundException("Embedded resource was not found: " + fileName);
            }

            using (Stream input = resourceInfo.Stream)
            using (FileStream output = File.Create(tempPath))
            {
                input.CopyTo(output);
            }

            return tempPath;
        }

        private void LoadSettings()
        {
            settings.Clear();
            settings["Theme"] = "Dark";

            try
            {
                if (!File.Exists(settingsPath))
                {
                    SaveSettings();
                    return;
                }

                foreach (string line in File.ReadAllLines(settingsPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith(";", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int separatorIndex = trimmed.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    string key = trimmed.Substring(0, separatorIndex).Trim();
                    string value = trimmed.Substring(separatorIndex + 1).Trim();
                    if (key.Length > 0)
                    {
                        settings[key] = value;
                    }
                }

                EnsureDefaultSettings();
            }
            catch (Exception ex)
            {
                settings["Theme"] = "Dark";
                Log("Could not load settings.ini: " + ex.Message);
            }
        }

        private void EnsureDefaultSettings()
        {
            bool changed = false;
            if (!settings.ContainsKey("Theme"))
            {
                settings["Theme"] = "Dark";
                changed = true;
            }

            if (changed)
            {
                SaveSettings();
            }
        }

        private string GetSetting(string key, string defaultValue)
        {
            string value;
            return settings.TryGetValue(key, out value) ? value : defaultValue;
        }

        private void SetSetting(string key, string value)
        {
            settings[key] = value;
            SaveSettings();
        }

        private void SaveSettings()
        {
            try
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("[Settings]");
                builder.AppendLine("Theme=" + GetSetting("Theme", "Dark"));
                File.WriteAllText(settingsPath, builder.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log("Could not save settings.ini: " + ex.Message);
            }
        }

        private void ApplyTheme(bool useDarkTheme)
        {
            ApplyTheme(useDarkTheme, true);
        }

        private void ApplyTheme(bool useDarkTheme, bool saveSetting)
        {
            darkTheme = useDarkTheme;
            DarkThemeMenuItem.IsChecked = useDarkTheme;
            LightThemeMenuItem.IsChecked = !useDarkTheme;

            SetBrush("WindowBrush", GetThemeColor("Window"));
            SetBrush("MenuBrush", GetThemeColor("Menu"));
            SetBrush("PanelBrush", GetThemeColor("Panel"));
            SetBrush("PanelAltBrush", GetThemeColor("PanelAlt"));
            SetBrush("HoverBrush", GetThemeColor("Hover"));
            SetBrush("PressedBrush", GetThemeColor("Pressed"));
            SetBrush("SelectedBrush", GetThemeColor("Selected"));
            SetBrush("BorderBrushDark", GetThemeColor("Border"));
            SetBrush("TextBrush", GetThemeColor("Text"));
            SetBrush("MutedTextBrush", GetThemeColor("MutedText"));
            SetBrush("DisabledTextBrush", GetThemeColor("DisabledText"));
            SetBrush("AccentBrush", GetThemeColor("Accent"));
            SetBrush("AccentHoverBrush", GetThemeColor("AccentHover"));
            SetBrush("AccentTextBrush", GetThemeColor("AccentText"));
            SetBrush("HeaderOverlayBrush", GetThemeColor("HeaderOverlay"));

            Background = (Brush)Resources["WindowBrush"];
            Foreground = (Brush)Resources["TextBrush"];
            TryUseDarkTitleBar(this, darkTheme);

            if (saveSetting)
            {
                SetSetting("Theme", useDarkTheme ? "Dark" : "Light");
            }
        }

        private void SetBrush(string resourceName, Color color)
        {
            Resources[resourceName] = new SolidColorBrush(color);
        }

        private Color GetThemeColor(string name)
        {
            if (darkTheme)
            {
                if (name == "Window") return Color.FromRgb(17, 19, 24);
                if (name == "Menu") return Color.FromRgb(13, 15, 20);
                if (name == "Panel") return Color.FromRgb(24, 27, 34);
                if (name == "PanelAlt") return Color.FromRgb(32, 36, 45);
                if (name == "Hover") return Color.FromRgb(42, 48, 59);
                if (name == "Pressed") return Color.FromRgb(31, 37, 48);
                if (name == "Selected") return Color.FromRgb(32, 63, 56);
                if (name == "Border") return Color.FromRgb(52, 58, 70);
                if (name == "Text") return Color.FromRgb(243, 244, 246);
                if (name == "MutedText") return Color.FromRgb(182, 190, 207);
                if (name == "DisabledText") return Color.FromRgb(111, 120, 137);
                if (name == "Accent") return Color.FromRgb(39, 211, 162);
                if (name == "AccentHover") return Color.FromRgb(57, 230, 180);
                if (name == "AccentText") return Color.FromRgb(6, 18, 15);
                if (name == "HeaderOverlay") return Color.FromArgb(102, 13, 15, 20);
            }

            if (name == "Window") return Color.FromRgb(246, 248, 251);
            if (name == "Menu") return Color.FromRgb(233, 237, 244);
            if (name == "Panel") return Color.FromRgb(255, 255, 255);
            if (name == "PanelAlt") return Color.FromRgb(241, 244, 249);
            if (name == "Hover") return Color.FromRgb(224, 231, 241);
            if (name == "Pressed") return Color.FromRgb(210, 220, 233);
            if (name == "Selected") return Color.FromRgb(215, 246, 237);
            if (name == "Border") return Color.FromRgb(184, 196, 212);
            if (name == "Text") return Color.FromRgb(21, 26, 36);
            if (name == "MutedText") return Color.FromRgb(83, 96, 115);
            if (name == "DisabledText") return Color.FromRgb(135, 146, 163);
            if (name == "Accent") return Color.FromRgb(20, 151, 116);
            if (name == "AccentHover") return Color.FromRgb(26, 177, 137);
            if (name == "AccentText") return Color.FromRgb(255, 255, 255);
            if (name == "HeaderOverlay") return Color.FromArgb(130, 255, 255, 255);

            return Colors.Transparent;
        }

        private void SetProcessingState(bool processing)
        {
            ProcessButton.IsEnabled = !processing;
            AddButton.IsEnabled = !processing;
            ClearButton.IsEnabled = !processing;
            ToolSelector.IsEnabled = !processing;
            CancelButton.IsEnabled = processing;
            JobProgressBar.IsIndeterminate = processing;
            JobProgressBar.Value = processing ? 0 : 0;
        }

        private void ReportProgress(int percent)
        {
            BackgroundWorker activeWorker = worker;
            if (activeWorker != null && activeWorker.WorkerReportsProgress)
            {
                activeWorker.ReportProgress(Math.Max(0, Math.Min(100, percent)));
            }
        }

        private void RequestCancel(string message)
        {
            if (cancelRequested)
            {
                return;
            }

            cancelRequested = true;
            Log(message);
            BackgroundWorker activeWorker = worker;
            if (activeWorker != null && activeWorker.IsBusy)
            {
                activeWorker.CancelAsync();
            }

            lock (processLock)
            {
                if (currentProcess != null)
                {
                    TryKillProcess(currentProcess);
                }
            }
        }

        private void TryKillProcess(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }
        }

        private sealed class JobRequest
        {
            public JobRequest(string selectedTool, string[] files)
            {
                SelectedTool = selectedTool;
                Files = files;
            }

            public string SelectedTool { get; private set; }
            public string[] Files { get; private set; }
        }

        private enum InstallMode
        {
            Portable,
            Installer
        }

        [DataContract]
        private sealed class GitHubReleaseInfo
        {
            [DataMember(Name = "tag_name")]
            public string TagName { get; set; }

            [DataMember(Name = "assets")]
            public ReleaseAssetInfo[] Assets { get; set; }
        }

        [DataContract]
        private sealed class ReleaseAssetInfo
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "browser_download_url")]
            public string BrowserDownloadUrl { get; set; }
        }

        private static void TryUseDarkTitleBar(Window window, bool useDarkTitleBar)
        {
            try
            {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                int enabled = useDarkTitleBar ? 1 : 0;
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

                int captionColor = useDarkTitleBar
                    ? ToColorRef(Color.FromRgb(17, 19, 24))
                    : ToColorRef(Color.FromRgb(246, 248, 251));
                int textColor = useDarkTitleBar
                    ? ToColorRef(Color.FromRgb(243, 244, 246))
                    : ToColorRef(Color.FromRgb(21, 26, 36));

                DwmSetWindowAttribute(handle, DwmCaptionColor, ref captionColor, sizeof(int));
                DwmSetWindowAttribute(handle, DwmTextColor, ref textColor, sizeof(int));
                RefreshWindowFrame(handle);
            }
            catch
            {
            }
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        private static void RefreshWindowFrame(IntPtr handle)
        {
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }

        private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmCaptionColor = 35;
        private const int DwmTextColor = 36;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
