using System;
using System.Globalization;
using System.IO.Ports;
using AudioMixerWin.Core.Models;

namespace AudioMixerWin.Core;

public class SerialManager
{
    private readonly SerialPort _port;
    private readonly InputMode _inputMode;

    public event Action<int, float>? KnobChanged;
    public event Action<int, int>? KnobDelta;

    public SerialManager(string comPort, int baudRate, InputMode inputMode = InputMode.Potentiometer)
    {
        _port = new SerialPort(comPort, baudRate);
        _port.DataReceived += OnData;
        _inputMode = inputMode;
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

        if (_inputMode == InputMode.RotaryEncoder)
        {
            if (int.TryParse(parts[1], out var delta))
                KnobDelta?.Invoke(knobIndex, delta);
        }
        else
        {
            if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                KnobChanged?.Invoke(knobIndex, Math.Clamp(value, 0f, 1f));
        }
    }
}
