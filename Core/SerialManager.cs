using System;
using System.Globalization;
using System.IO.Ports;

namespace AudioMixerWin.Core;

public class SerialManager
{
    private readonly SerialPort _port;

    public event Action<int, float>? KnobChanged;

    public SerialManager(string comPort, int baudRate)
    {
        _port = new SerialPort(comPort, baudRate);
        _port.DataReceived += OnData;
    }

    public void Start()
    {
        _port.Open();
    }

    public void Stop()
    {
        try
        {
            _port.DataReceived -= OnData;
            if (_port.IsOpen)
                _port.Close();
            _port.Dispose();
        }
        catch { }
    }

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var line = _port.ReadLine().Trim();

            HandleCommand(line);
        }
        catch { }
    }

    private void HandleCommand(string cmd)
    {
        var parts = cmd.Split(':');
        if (parts.Length != 2)
            return;

        if (!int.TryParse(parts[0], out var knobIndex))
            return;

        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return;

        KnobChanged?.Invoke(knobIndex, Math.Clamp(value, 0f, 1f));
    }
}
