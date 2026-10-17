using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeskHardwareWidget
{
    public partial class GpuCard : UserControl
    {
        private readonly List<double> _history = GraphRenderer.NewHistory();
        public string DeviceId { get; private set; }
        public event EventHandler MenuRequested;
        public GpuCard(string deviceId) { InitializeComponent(); DeviceId = deviceId; }
        public void UpdateGpu(GpuSnapshot gpu, int index)
        {
            txtTitle.Text = "GPU " + (index + 1);
            txtName.Text = gpu.Name; txtName.ToolTip = gpu.Name;
            txtLoad.Text = gpu.HasLoad ? string.Format("{0:0} %", gpu.LoadPercent) : "— %";
            txtTemperature.Text = gpu.TemperatureText; txtTemperature.ToolTip = gpu.TemperatureText;
            txtMemory.Text = gpu.MemoryText; txtMemory.ToolTip = gpu.MemoryText;
            GraphRenderer.Add(_history, gpu.HasLoad ? gpu.LoadPercent : double.NaN);
            GraphRenderer.Draw(graphLine, graphFill, _history, 100);
        }
        private void Dots_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (MenuRequested != null) MenuRequested(this, EventArgs.Empty);
        }
    }
}
