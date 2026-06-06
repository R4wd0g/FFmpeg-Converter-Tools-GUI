using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FFmpegConverterGUI
{
    public partial class MainWindow : Window
    {
        private const string BtbNFfmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-06-05-13-55/ffmpeg-N-124841-gb355200263-win64-gpl.zip";
        private readonly object processLock = new object();
        private readonly Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string settingsPath;
        private BackgroundWorker worker;
        private Process currentProcess;
        private MediaPlayer completionSoundPlayer;
        private volatile bool cancelRequested;
        private bool darkTheme = true;

        public MainWindow()
        {
            settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
            InitializeComponent();
            SourceInitialized += MainWindow_SourceInitialized;
            DropArea.PreviewDragOver += DropArea_PreviewDragOver;
            DropArea.Drop += DropArea_Drop;
            ToolSelector.SelectionChanged += ToolSelector_SelectionChanged;
            ConvertTypeSelector.SelectionChanged += ConvertTypeSelector_SelectionChanged;
            AddButton.Click += AddButton_Click;
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
            about.Height = 390;
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
            logo.Source = new BitmapImage(new Uri("pack://application:,,,/icon.ico", UriKind.Absolute));
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
            title.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(title);

            TextBlock credits = new TextBlock();
            credits.Text = "Created by R4wd0G\n\nA comeback project that turns a practical FFmpeg batch toolkit into a native Windows GUI for faster everyday media conversion.";
            credits.TextAlignment = TextAlignment.Center;
            credits.TextWrapping = TextWrapping.Wrap;
            credits.Foreground = new SolidColorBrush(GetThemeColor("MutedText"));
            credits.Margin = new Thickness(0, 0, 0, 20);
            panel.Children.Add(credits);

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
            panel.Children.Add(closeButton);

            shell.Child = panel;
            about.Content = shell;

            bool closeStarted = false;
            bool allowClose = false;
            DispatcherTimer aboutFadeTimer = null;
            Action beginClose = delegate
            {
                if (closeStarted)
                {
                    return;
                }

                closeStarted = true;
                closeButton.IsHitTestVisible = false;
                closeButton.Opacity = 0.65;

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
                if (aboutFadeTimer != null)
                {
                    aboutFadeTimer.Stop();
                }
            };
            about.ShowDialog();
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
