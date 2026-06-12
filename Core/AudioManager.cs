using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AudioMixerWin.Core.Models;
using NAudio.CoreAudioApi;

namespace AudioMixerWin.Core;

public class AudioManager
    {
        private readonly MMDevice _device;

        public AudioManager()
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        public List<AudioSession> GetSessions()
        {
            var result = new List<AudioSession>();
            var sessions = _device.AudioSessionManager.Sessions;

            Console.WriteLine("Audio sessions detected:\n");

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];

                try
                {
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);
                    
                    result.Add(new AudioSession
                    {
                        ProcessName = process.ProcessName,
                        DisplayName = process.ProcessName,
                        Volume = session.SimpleAudioVolume.Volume,
                    });

                    Console.WriteLine(
                        $"{process.ProcessName} - Volume: {session.SimpleAudioVolume.Volume}"
                    );
                }
                catch
                {
                    Console.WriteLine($"System session - Volume: {session.SimpleAudioVolume.Volume}");
                }
            }

            return result
                .GroupBy(x => x!.ProcessName)
                .Select(g => g.First()!)
                .OrderBy(x => x.ProcessName)
                .ToList();;
        }
        
        public float GetVolume(string processName)
        {
            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];

                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(
                            processName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return session.SimpleAudioVolume.Volume;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return 0;
        }
        
        public void SetVolume(string processName, float volume)
        {
            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];

                try
                {
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    {
                        session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Session error: {ex.Message}");
                }
            }
        }
    }