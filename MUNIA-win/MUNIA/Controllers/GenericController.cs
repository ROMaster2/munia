﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;
using MUNIA.Util;

namespace MUNIA.Controllers {
	public class GenericController : IController {
		// The controller is the DeviceItem on HidDevice. One HidDevice may expose multiple DeviceItems.
		private readonly HidDevice HidDevice;
		private readonly DeviceItem DeviceItem;
		public ControllerType Type => ControllerType.Generic;

		public GenericController(HidDevice device, DeviceItem deviceItem) {
			HidDevice = device;
			DeviceItem = deviceItem;
		}

		// internal state
		protected readonly List<bool> _buttons = new List<bool>();
		protected readonly List<double> _axes = new List<double>();
		protected readonly List<Hat> _hats = new List<Hat>();
		public ControllerState GetState() => new ControllerState(_axes, _buttons, _hats);
		public event EventHandler StateUpdated;

		public string DevicePath => HidDevice.DevicePath;
		public virtual string Name => HidDevice.ProductName;

		// stream, parser and report cache only available while device activated
		private HidStream _stream;
		private DeviceItemInputParser _inputParser;
		private readonly Dictionary<byte, Report> _reportCache = new Dictionary<byte, Report>();

		public bool Activate() {
			_inputParser = DeviceItem.CreateDeviceItemInputParser();
			try {
				_stream?.Dispose();
				_stream = HidDevice.Open();
				_stream.ReadTimeout = Timeout.Infinite;

				// determine number of buttons, axes, hats
				DetermineCapabilities();

				byte[] buffer = new byte[HidDevice.GetMaxInputReportLength()];
				var sb = new MuniaController.StreamAndBuffer { buffer = buffer, stream = _stream };

				_stream.BeginRead(buffer, 0, buffer.Length, Callback, sb);
				return true;
			}
			catch {
				return false;
			}
		}

		private void DetermineCapabilities() {
			_hats.Clear();
			_buttons.Clear();
			_axes.Clear();
			int numHats = 0;
			int numButtons = 0;
			int numAxes = 0;
			foreach (var report in DeviceItem.InputReports) {
				foreach (var dataItem in report.DataItems) {
					foreach (Usage usage in dataItem.Usages.GetAllValues()) {
						if (Usage.Button1 <= usage && usage <= Usage.Button31) {
							int btnIdx = (int)(usage - (int)Usage.Button1);
							numButtons = Math.Max(numButtons, btnIdx + 1);
						}
						else if (usage == Usage.GenericDesktopHatSwitch) {
							numHats++;
						}
						else if (Usage.GenericDesktopX <= usage && usage <= Usage.GenericDesktopRz) {
							int axisIdx = (int)(usage - (int)Usage.GenericDesktopX);
							numAxes = Math.Max(numAxes, axisIdx + 1);
						}
						else {
							// unrecognized usage
							Debug.WriteLine("Unrecognized usage: " + usage);
						}
					}
				}
			}

			_hats.EnsureSize(numHats);
			numButtons += 4 * numHats; // also map each direction of hat as separate button
			_buttons.EnsureSize(numButtons);
			_axes.EnsureSize(numAxes);
		}

		public void Deactivate() {
			_stream?.Dispose();
			_stream = null;
			_reportCache.Clear();
		}

		private void Callback(IAsyncResult ar) {
			var sb = (MuniaController.StreamAndBuffer)ar.AsyncState;
			try {
				int numBytes = sb.stream.EndRead(ar);
				if (numBytes > 0) {
					if (Parse(sb.buffer))
						StateUpdated?.Invoke(this, EventArgs.Empty);
					sb.stream.BeginRead(sb.buffer, 0, sb.buffer.Length, Callback, sb);
				}
			}
			catch (IOException exc) {
				_stream = null;
				Debug.WriteLine("IOException: " + exc.Message);
				sb.stream.Dispose();
			}
			catch (ObjectDisposedException) { }
			catch (NullReferenceException) { }
		}

		private bool Parse(byte[] reportBuffer) {
			try {
				byte reportId = reportBuffer[0];
				if (!_reportCache.TryGetValue(reportId, out Report report)) {
					_reportCache[reportId] = report = HidDevice.GetReportDescriptor().GetReport(ReportType.Input, reportId);
				}

				// Parse the report if possible.
				if (_inputParser.TryParseReport(reportBuffer, 0, report)) {
					while (_inputParser.HasChanged) {
						int changedIndex = _inputParser.GetNextChangedIndex();
						var dataValue = _inputParser.GetValue(changedIndex);
						
						Usage usage = (Usage)dataValue.Usages.FirstOrDefault();
						if (Usage.Button1 <= usage && usage <= Usage.Button31) {
							int btnIdx = (int)(usage - (int)Usage.Button1);
							_buttons.EnsureSize(btnIdx + 1);
							_buttons[btnIdx] = dataValue.GetLogicalValue() != 0;
							int val = dataValue.GetLogicalValue();
						}
						else if (usage == Usage.GenericDesktopHatSwitch) {
							// can only support 1 hat..
							_hats.EnsureSize(1);
							//Debug.WriteLine("Logical hat: " + dataValue.GetLogicalValue());
							_hats[0] = ControllerState.HatLookup[(byte)dataValue.GetLogicalValue()];
						}
						else if (Usage.GenericDesktopX <= usage && usage <= Usage.GenericDesktopRz) {
							int axisIdx = (int)(usage - (int)Usage.GenericDesktopX);
							_axes.EnsureSize(axisIdx + 1);
							_axes[axisIdx] = ScaleAxis(dataValue);
						}
						else {
							// unrecognized usage
							Debug.WriteLine("Unrecognized usage: " + usage);
						}
					}

					// for skinning simplicity sake, map hats also to buttons
					int btn = _buttons.Count - 4 * _hats.Count;
					for (int i = 0; i < _hats.Count; i++) {
						// UP DOWN LEFT RIGHT
						_buttons[btn++] = _hats[i].HasFlag(Hat.Up);
						_buttons[btn++] = _hats[i].HasFlag(Hat.Down);
						_buttons[btn++] = _hats[i].HasFlag(Hat.Left);
						_buttons[btn++] = _hats[i].HasFlag(Hat.Right);
					}

					return true;
				}
			}
			catch { }
			return false;
		}

		private double ScaleAxis(DataValue dataValue) {
			// first see if this item has a logical min/max defined
			var di = dataValue.DataItem;
			double val;
			if (di.LogicalMinimum < di.LogicalMaximum) {
				val = dataValue.GetLogicalValue() / (double)(di.LogicalMaximum - di.LogicalMaximum);
			}
			else {
				int range = 1 << di.ElementBits;
				val = dataValue.GetLogicalValue() / (double)range;
			}

			if (IsTrigger((Usage)dataValue.Usages.First()))
				val -= 0.5;

			return val;
		}

		private bool IsTrigger(Usage usage) {
			return usage == Usage.GenericDesktopRz || usage == Usage.GenericDesktopZ;
		}

		public bool IsActive => _stream != null && _stream.CanRead;
		public bool IsAvailable {
			get {
				HidStream s;
				var ret = HidDevice.TryOpen(out s);
				if (!ret) return false;
				ret = s.CanRead;
				ret &= HidDevice.GetReportDescriptor().DeviceItems.Contains(DeviceItem);
				s.Close();
				return ret;
			}
		}

		public static IEnumerable<GenericController> ListDevices() {
			// find all devices with a gamepad or joystick usage page
			foreach (var dev in DeviceList.Local.GetHidDevices()) {

				var reportDescriptor = dev.GetReportDescriptor();
				foreach (var deviceItem in reportDescriptor.DeviceItems) {
					bool isJoystickOrGamepad = deviceItem.Usages.GetAllValues().Contains((uint)Usage.GenericDesktopJoystick) ||
												deviceItem.Usages.GetAllValues().Contains((uint)Usage.GenericDesktopGamepad);
					if (isJoystickOrGamepad && deviceItem.InputReports.Any())
						yield return new GenericController(dev, deviceItem);
				}
			}

		}

		public override string ToString() => Name;
	}
}
