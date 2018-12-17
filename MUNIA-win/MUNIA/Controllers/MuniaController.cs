﻿using HidSharp;
using MUNIA.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MUNIA.Controllers
{
    public abstract class MuniaController : IController, IDisposable
    {
        /// <summary>
        /// Can be used to register for WM_INPUT messages and parse them.
        /// For testing purposes it can also be used to solely register for WM_INPUT messages.
        /// </summary>

        public HidDevice HidDevice;
        private HidStream _stream;
        public string DevicePath => HidDevice.DevicePath;
        public virtual string Name => HidDevice.ProductName;
        public abstract ControllerType Type { get; }

        protected readonly List<bool> _buttons = new List<bool>();
        protected readonly List<int> _axes = new List<int>();
        protected readonly List<Hat> _hats = new List<Hat>();
        public ControllerState GetState() => new ControllerState(_axes, _buttons, _hats);

        const int LOG_LENGTH = 2097152; // About ten hours worth of inputs at 60Hz, assuming it's being filled the whole time.
        public List<UltraTime> InputLog = new List<UltraTime>(2097152);
        public static Stopwatch LogWatch = Stopwatch.StartNew();

        public event EventHandler StateUpdated;

        public class UltraTime
        {
            public readonly TimeSpan TimeSpan;
            public readonly DateTime DateTime;
            public readonly AtomicDateTime AtomicDateTime;
            public readonly TimeSpan Jitter;
            public byte[] Inputs;
            public UltraTime()
            {
                TimeSpan = LogWatch.Elapsed;
                DateTime = DateTime.UtcNow;
                AtomicDateTime = TimeStamp.CurrentDateTime;
                Jitter = LogWatch.Elapsed - TimeSpan;
                Inputs = new byte[5];
            }
            override public string ToString()
            {
                return TimeSpan.Ticks.ToString("x16").Substring(4) +
                DateTime.Ticks.ToString("x16") +
                AtomicDateTime.Time.Ticks.ToString("x16") +
                (AtomicDateTime.SyncedWithAtomicClock ? "01" : "00") +
                Jitter.Ticks.ToString("x16").Substring(8) +
                Inputs[1].ToString("x2") +
                Inputs[2].ToString("x2") +
                Inputs[3].ToString("x2") +
                Inputs[4].ToString("x2");
            }
            public byte[] M64()
            {
                return new byte[4]
                {
                    (byte)(Inputs[1] ^ 0b00001000),
                    Inputs[2],
                    (byte)(sbyte)(Inputs[3] - 128),
                    (byte)(sbyte)-(Inputs[4] - 128)
                };
            }
        }

        protected MuniaController(HidDevice hidDevice)
        {
            this.HidDevice = hidDevice;
        }
        public override string ToString()
        {
            return Name;
        }

        public static IEnumerable<MuniaController> ListDevices()
        {
            var ldr = new HidDeviceLoader();

            // MUNIA devices with 0x1209 VID
            foreach (var device in ldr.GetDevices(0x1209))
            {
                if (device.ProductName == "NinHID NGC")
                {
                    yield return new MuniaNgc(device);
                }
                else if (device.ProductName == "NinHID N64")
                {
                    yield return new MuniaN64(device);
                }
                else if (device.ProductName == "NinHID SNES")
                {
                    yield return new MuniaSnes(device);
                }
            }

            // MUSIA devices
            foreach (var device in ldr.GetDevices(0x1209, 0x8844))
            {
                if (device.ProductName == "MUSIA PS2 controller")
                {
                    yield return new MusiaPS2(device);
                }
            }


            // legacy VID/PID combo from microchip
            foreach (var device in ldr.GetDevices(0x04d8, 0x0058))
            {
                if (device.ProductName == "NinHID NGC" || device.GetMaxInputReportLength() == 9 && device.GetMaxOutputReportLength() == 0)
                {
                    yield return new MuniaNgc(device);
                }
                else if (device.ProductName == "NinHID N64" || device.GetMaxInputReportLength() == 5)
                {
                    yield return new MuniaN64(device);
                }
                else if (device.ProductName == "NinHID SNES" || device.GetMaxInputReportLength() == 3)
                {
                    yield return new MuniaSnes(device);
                }
            }

        }

        public static IEnumerable<ConfigInterface> GetConfigInterfaces()
        {
            return GetMuniaConfigInterfaces().OfType<ConfigInterface>()
                .Union(GetMusiaConfigInterfaces());
        }

        public static IEnumerable<MuniaConfigInterface> GetMuniaConfigInterfaces()
        {
            var ldr = new HidDeviceLoader();
            var candidates = ldr.GetDevices(0x04d8, 0x0058)
                .Union(ldr.GetDevices(0x1209));
            foreach (HidDevice dev in candidates.Where(device => device.ProductName == "NinHID CFG"
                || device.GetMaxInputReportLength() == device.GetMaxOutputReportLength()
                    && device.GetMaxOutputReportLength() == 9))
                yield return new MuniaConfigInterface(dev);
        }

        public static IEnumerable<HidDevice> GetMuniaBootloaderInterfaces()
        {
            var ldr = new HidDeviceLoader();
            return ldr.GetDevices(0x04d8, 0x003c);
        }

        public static IEnumerable<MusiaConfigInterface> GetMusiaConfigInterfaces()
        {
            var ldr = new HidDeviceLoader();
            foreach (HidDevice dev in ldr.GetDevices(0x1209, 0x8844).Where(d => d.ProductName.Contains("config")))
                yield return new MusiaConfigInterface(dev);
        }

        public class StreamAndBuffer
        {
            public byte[] buffer;
            public Stream stream;
        }

        /// <summary>
        /// Registers device for receiving inputs.
        /// </summary>
        public bool Activate()
        {
            try
            {
                _stream?.Dispose();
                _stream = HidDevice.Open();
                _stream.ReadTimeout = Timeout.Infinite;

                byte[] buffer = new byte[HidDevice.GetMaxInputReportLength()];
                var sb = new StreamAndBuffer { buffer = buffer, stream = _stream };

                _stream.BeginRead(buffer, 0, buffer.Length, Callback, sb);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Deactivate()
        {
            _stream?.Dispose();
            _stream = null;
        }
        public bool IsActive => _stream != null && _stream.CanRead;
        public bool IsAvailable
        {
            get
            {
                HidStream s;
                var ret = HidDevice.TryOpen(out s);
                if (!ret) return false;
                ret = s.CanRead;
                s.Close();
                return ret;
            }
        }

        private void Callback(IAsyncResult ar)
        {
            var log = new UltraTime();
            var sb = (StreamAndBuffer)ar.AsyncState;
            try
            {
                int numBytes = sb.stream.EndRead(ar);
                if (numBytes > 0)
                {
                    if (Parse(sb.buffer))
                        StateUpdated?.Invoke(this, EventArgs.Empty);
                    sb.stream.BeginRead(sb.buffer, 0, sb.buffer.Length, Callback, sb);
                    sb.buffer.CopyTo(log.Inputs, 0);
                    InputLog.Add(log);

                    if (sb.buffer[1] == 4)
                    {
                        var fuckyoumore = InputLog.ToArray();
                        using (var ts = new StringWriter())
                        using (var bs = new BinaryWriter(File.Open(@"C:\inputLogRaw.txt", FileMode.Create)))
                        {
                            foreach (var x in fuckyoumore)
                            {
                                ts.WriteLine(x.ToString());
                                bs.Write(x.M64());
                            }
                            File.WriteAllText(@"C:\inputLog.txt", ts.ToString());
                        }
                    }

                }
                if (numBytes != 5)
                    throw new Exception("test");
            }
            catch (IOException exc)
            {
                _stream = null;
                Debug.WriteLine("IOException: " + exc.Message);
                sb.stream.Dispose();
            }
            catch (ObjectDisposedException) { }
            catch (NullReferenceException) { }
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }

        protected abstract bool Parse(byte[] ev);

    }
}