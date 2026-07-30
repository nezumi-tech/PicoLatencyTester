using System;
using System.IO;
using System.Windows;
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

        public MainWindow()
        {
            InitializeComponent();
            LoadDevices();
        }

        private void LoadDevices()
        {
            // リストを一度クリアして重複を防ぐ
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

        // --- デバイス再検出ボタンのイベントハンドラを追加 ---
        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (isRecording)
            {
                MessageBox.Show("録音中はデバイスの再検出ができません。録音を停止してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LoadDevices();
            TxtStatus.Text = "デバイスリストを更新しました";
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (!isRecording)
            {
                StartRecording();
            }
            else
            {
                StopRecording();
            }
        }

        private void StartRecording()
        {
            int devIndexL = CmbDeviceL.SelectedIndex;
            int devIndexR = CmbDeviceR.SelectedIndex;

            if (devIndexL < 0 || devIndexR < 0) return;

            streamL = new MemoryStream();
            streamR = new MemoryStream();

            // デバイスをこの瞬間だけオープンする (遅延蓄積の再現のため)
            waveInL = new WaveInEvent { DeviceNumber = devIndexL, WaveFormat = new WaveFormat(48000, 16, 2) };
            waveInR = new WaveInEvent { DeviceNumber = devIndexR, WaveFormat = new WaveFormat(48000, 16, 2) };

            waveInL.DataAvailable += (s, a) => streamL.Write(a.Buffer, 0, a.BytesRecorded);
            waveInR.DataAvailable += (s, a) => streamR.Write(a.Buffer, 0, a.BytesRecorded);

            waveInL.StartRecording();
            waveInR.StartRecording();

            isRecording = true;
            BtnRecord.Content = "録音停止";
            BtnRefresh.IsEnabled = false; // 録音中は再検出ボタンを無効化
            BtnPreview.IsEnabled = false;
            BtnSave.IsEnabled = false;
            TxtStatus.Text = "録音中... (デバイス接続済み)";
        }

        private void StopRecording()
        {
            // 録音停止時にデバイスを完全に解放し、ストリームの読み取りを止める
            waveInL?.StopRecording();
            waveInR?.StopRecording();

            waveInL?.Dispose();
            waveInR?.Dispose();

            waveInL = null;
            waveInR = null;

            isRecording = false;
            BtnRecord.Content = "録音開始";
            BtnRefresh.IsEnabled = true; // 録音停止後に再検出ボタンを有効化
            BtnPreview.IsEnabled = true;
            BtnSave.IsEnabled = true;
            TxtStatus.Text = "待機中 (デバイス完全解放済み)";
        }

        private byte[] MixToStereo()
        {
            if (streamL == null || streamR == null) return Array.Empty<byte>();

            byte[] dataL = streamL.ToArray();
            byte[] dataR = streamR.ToArray();

            int minLength = Math.Min(dataL.Length, dataR.Length);
            byte[] mixedData = new byte[minLength];

            int modeL = CmbModeL.SelectedIndex; // 0:Mono, 1:Left, 2:Right
            int modeR = CmbModeR.SelectedIndex; // 0:Mono, 1:Left, 2:Right

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

            return mode switch
            {
                1 => left,
                2 => right,
                _ => (short)((left + right) / 2) // Mono
            };
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
    }
}