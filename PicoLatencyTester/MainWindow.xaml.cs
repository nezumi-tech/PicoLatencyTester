using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NAudio.Wave;

namespace PicoLatencyTester
{
    public partial class MainWindow : Window
    {
        private WaveInEvent? waveInL;
        private WaveInEvent? waveInR;
        private MemoryStream? streamL;
        private MemoryStream? streamR;
        private bool isRecording = false;

        // --- 自動化用の変数 ---
        private DispatcherTimer logMonitorTimer;
        private string latestLogFilePath = string.Empty;
        private long lastLogPosition = 0;
        private bool isAutoProcessing = false;
        private int worldJoinCount = 0;

        public MainWindow()
        {
            InitializeComponent();
            LoadDevices();

            // ログ監視用タイマーの初期化 (1秒間隔)
            logMonitorTimer = new DispatcherTimer();
            logMonitorTimer.Interval = TimeSpan.FromSeconds(1);
            logMonitorTimer.Tick += LogMonitorTimer_Tick;
        }

        private void LoadDevices()
        {
            CmbDeviceL.Items.Clear();
            CmbDeviceR.Items.Clear();

            int deviceCount = WaveInEvent.DeviceCount;
            for (int i = 0; i < deviceCount; i++)
            {
                var deviceInfo = WaveInEvent.GetCapabilities(i);
                CmbDeviceL.Items.Add(deviceInfo.ProductName);
                CmbDeviceR.Items.Add(deviceInfo.ProductName);
            }
            if (deviceCount > 0)
            {
                CmbDeviceL.SelectedIndex = 0;
                CmbDeviceR.SelectedIndex = 0;
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (isRecording || isAutoProcessing)
            {
                MessageBox.Show("録音中または自動処理中はデバイスの再検出ができません。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LoadDevices();
            TxtStatus.Text = "デバイスリストを更新しました";
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (!isRecording) StartRecording();
            else StopRecording();
        }

        private void StartRecording()
        {
            int devIndexL = CmbDeviceL.SelectedIndex;
            int devIndexR = CmbDeviceR.SelectedIndex;
            if (devIndexL < 0 || devIndexR < 0) return;

            streamL = new MemoryStream();
            streamR = new MemoryStream();

            waveInL = new WaveInEvent { DeviceNumber = devIndexL, WaveFormat = new WaveFormat(48000, 16, 2) };
            waveInR = new WaveInEvent { DeviceNumber = devIndexR, WaveFormat = new WaveFormat(48000, 16, 2) };

            waveInL.DataAvailable += (s, a) => streamL.Write(a.Buffer, 0, a.BytesRecorded);
            waveInR.DataAvailable += (s, a) => streamR.Write(a.Buffer, 0, a.BytesRecorded);

            waveInL.StartRecording();
            waveInR.StartRecording();

            isRecording = true;
            if (ChkAutoMode.IsChecked != true)
            {
                BtnRecord.Content = "手動録音 停止";
                BtnRefresh.IsEnabled = false;
                BtnPreview.IsEnabled = false;
                BtnSave.IsEnabled = false;
                TxtStatus.Text = "録音中... (デバイス接続済み)";
            }
        }

        private void StopRecording()
        {
            waveInL?.StopRecording();
            waveInR?.StopRecording();

            waveInL?.Dispose();
            waveInR?.Dispose();

            waveInL = null;
            waveInR = null;

            isRecording = false;
            if (ChkAutoMode.IsChecked != true)
            {
                BtnRecord.Content = "手動録音";
                BtnRefresh.IsEnabled = true;
                BtnPreview.IsEnabled = true;
                BtnSave.IsEnabled = true;
                TxtStatus.Text = "待機中 (デバイス完全解放済み)";
            }
        }

        private byte[] MixToStereo()
        {
            if (streamL == null || streamR == null) return Array.Empty<byte>();

            byte[] dataL = streamL.ToArray();
            byte[] dataR = streamR.ToArray();

            int minLength = Math.Min(dataL.Length, dataR.Length);
            byte[] mixedData = new byte[minLength];

            int modeL = Dispatcher.Invoke(() => CmbModeL.SelectedIndex);
            int modeR = Dispatcher.Invoke(() => CmbModeR.SelectedIndex);

            for (int i = 0; i < minLength; i += 4)
            {
                short sampleL = ExtractSample(dataL, i, modeL);
                short sampleR = ExtractSample(dataR, i, modeR);

                mixedData[i] = (byte)(sampleL & 0xFF);
                mixedData[i + 1] = (byte)((sampleL >> 8) & 0xFF);
                mixedData[i + 2] = (byte)(sampleR & 0xFF);
                mixedData[i + 3] = (byte)((sampleR >> 8) & 0xFF);
            }
            return mixedData;
        }

        private short ExtractSample(byte[] data, int index, int mode)
        {
            short left = BitConverter.ToInt16(data, index);
            short right = BitConverter.ToInt16(data, index + 2);
            return mode switch { 1 => left, 2 => right, _ => (short)((left + right) / 2) };
        }

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            byte[] mixed = MixToStereo();
            if (mixed.Length == 0) return;
            IWaveProvider provider = new RawSourceWaveStream(new MemoryStream(mixed), new WaveFormat(48000, 16, 2));
            WaveOutEvent waveOut = new WaveOutEvent();
            waveOut.Init(provider);
            waveOut.Play();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            byte[] mixed = MixToStereo();
            if (mixed.Length == 0) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { DefaultExt = ".wav", Filter = "WAV Files (*.wav)|*.wav" };
            if (dlg.ShowDialog() == true)
            {
                using (var writer = new WaveFileWriter(dlg.FileName, new WaveFormat(48000, 16, 2)))
                {
                    writer.Write(mixed, 0, mixed.Length);
                }
                MessageBox.Show("保存しました。");
            }
        }

        // ==========================================
        // 以下、自動化関連のロジック
        // ==========================================

        private void ChkAutoMode_Checked(object sender, RoutedEventArgs e)
        {
            BtnRecord.IsEnabled = false;
            BtnPreview.IsEnabled = false;
            BtnSave.IsEnabled = false;
            BtnRefresh.IsEnabled = false;

            // C:\Users\<ユーザー名>\AppData\LocalLow\VRChat\VRChat パスを正確に構築
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string logDir = Path.Combine(userProfile, @"AppData\LocalLow\VRChat\VRChat");

            if (Directory.Exists(logDir))
            {
                var dirInfo = new DirectoryInfo(logDir);

                // output_log_*.txt に一致する最新の更新日時のファイルを取得
                var latestLog = dirInfo.GetFiles("output_log_*.txt")
                                       .OrderByDescending(f => f.LastWriteTime)
                                       .FirstOrDefault();

                if (latestLog != null)
                {
                    latestLogFilePath = latestLog.FullName;

                    // 既存のログを読み飛ばし、監視を開始した時点からの追記分を対象にする
                    lastLogPosition = latestLog.Length;

                    logMonitorTimer.Start();
                    TxtStatus.Text = $"自動モード: ログ監視中... ({latestLog.Name})";
                    return;
                }
            }

            // ログが見つからない場合
            ChkAutoMode.IsChecked = false;
            BtnRecord.IsEnabled = true;
            BtnPreview.IsEnabled = true;
            BtnSave.IsEnabled = true;
            BtnRefresh.IsEnabled = true;

            MessageBox.Show($"VRChatのログファイルが見つかりませんでした。\n検索パス: {logDir}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void ChkAutoMode_Unchecked(object sender, RoutedEventArgs e)
        {
            logMonitorTimer.Stop();
            BtnRecord.IsEnabled = true;
            BtnPreview.IsEnabled = true;
            BtnSave.IsEnabled = true;
            BtnRefresh.IsEnabled = true;
            TxtStatus.Text = "自動モード解除: 待機中";
        }

        private void LogMonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (isAutoProcessing || string.IsNullOrEmpty(latestLogFilePath) || !File.Exists(latestLogFilePath)) return;

            try
            {
                // VRChatが書き込み中でも安全にアクセスできるよう FileShare.ReadWrite | FileShare.Delete を指定
                using (FileStream fs = new FileStream(latestLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length == lastLogPosition) return; // 追記なし
                    if (fs.Length < lastLogPosition) lastLogPosition = 0; // ログリセット時

                    fs.Seek(lastLogPosition, SeekOrigin.Begin);
                    using (StreamReader reader = new StreamReader(fs))
                    {
                        string newLogs = reader.ReadToEnd();
                        lastLogPosition = fs.Position;

                        // ワールド参加イベント ("Finished entering world.") の検知
                        if (newLogs.Contains("Finished entering world."))
                        {
                            _ = ExecuteAutoRecordSequence();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // デバッグ出力
                System.Diagnostics.Debug.WriteLine($"Log Read Error: {ex.Message}");
            }
        }

        private async Task ExecuteAutoRecordSequence()
        {
            isAutoProcessing = true;
            worldJoinCount++;

            Dispatcher.Invoke(() => TxtStatus.Text = $"ワールド移動を検知 [#{worldJoinCount}]: 安定化待ち (5秒)...");

            // 1. 安定化のための待機
            await Task.Delay(5000);

            // 2. 開始の通知音
            _ = Task.Run(() => Console.Beep(1000, 300));

            // 3. 録音開始
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = $"自動録音中 [#{worldJoinCount}] (10秒間)...";
                StartRecording();
            });

            // 4. 10秒間録音
            await Task.Delay(10000);

            // 5. 録音停止
            Dispatcher.Invoke(() => StopRecording());

            // 6. ファイルの自動保存
            string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AutoRecords");
            Directory.CreateDirectory(outputDir);
            string fileName = $"Record_{worldJoinCount:D3}.wav";
            string filePath = Path.Combine(outputDir, fileName);

            byte[] mixed = MixToStereo();
            if (mixed.Length > 0)
            {
                using (var writer = new WaveFileWriter(filePath, new WaveFormat(48000, 16, 2)))
                {
                    writer.Write(mixed, 0, mixed.Length);
                }
            }

            // 7. 完了の通知音
            _ = Task.Run(() => Console.Beep(500, 300));

            Dispatcher.Invoke(() => TxtStatus.Text = $"保存完了: {fileName} -> ログ監視再開");

            // 次のトリガーが連続で誤爆しないためのクールダウン
            await Task.Delay(2000);
            isAutoProcessing = false;
        }
    }
}