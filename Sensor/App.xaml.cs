﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

using Windows.Devices.Enumeration;
using Microsoft.Maker.RemoteWiring;
using Microsoft.Maker.Serial;
using Waher.Events;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Script;

namespace Sensor
{
	/// <summary>
	/// Provides application-specific behavior to supplement the default Application class.
	/// </summary>
	sealed partial class App : Application
	{
		private UsbSerial arduinoUsb = null;
		private RemoteDevice arduino = null;
		private Timer sampleTimer = null;

		private const int windowSize = 10;
		private const int spikePos = windowSize / 2;
		private int?[] windowA0 = new int?[windowSize];
		private int nrA0 = 0;
		private int sumA0 = 0;

		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
		{
			this.InitializeComponent();
			this.Suspending += OnSuspending;
		}

		/// <summary>
		/// Invoked when the application is launched normally by the end user.  Other entry points
		/// will be used such as when the application is launched to open a specific file.
		/// </summary>
		/// <param name="e">Details about the launch request and process.</param>
		protected override void OnLaunched(LaunchActivatedEventArgs e)
		{
			Frame rootFrame = Window.Current.Content as Frame;

			// Do not repeat app initialization when the Window already has content,
			// just ensure that the window is active
			if (rootFrame == null)
			{
				// Create a Frame to act as the navigation context and navigate to the first page
				rootFrame = new Frame();

				rootFrame.NavigationFailed += OnNavigationFailed;

				if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
				{
					//TODO: Load state from previously suspended application
				}

				// Place the frame in the current Window
				Window.Current.Content = rootFrame;
			}

			if (e.PrelaunchActivated == false)
			{
				if (rootFrame.Content == null)
				{
					// When the navigation stack isn't restored navigate to the first page,
					// configuring the new page by passing required information as a navigation
					// parameter
					rootFrame.Navigate(typeof(MainPage), e.Arguments);
				}
				// Ensure the current window is active
				Window.Current.Activate();
				Task.Run((Action)this.Init);
			}
		}

		private async void Init()
		{
			try
			{
				Log.Informational("Starting application.");
				Types.Initialize(typeof(FilesProvider).GetTypeInfo().Assembly, typeof(App).GetTypeInfo().Assembly);
				Database.Register(new FilesProvider(Windows.Storage.ApplicationData.Current.LocalFolder.Path +
					Path.DirectorySeparatorChar + "Data", "Default", 8192, 1000, 8192, Encoding.UTF8, 10000));

				DeviceInformationCollection Devices = await UsbSerial.listAvailableDevicesAsync();
				foreach (DeviceInformation DeviceInfo in Devices)
				{
					if (DeviceInfo.IsEnabled && DeviceInfo.Name.StartsWith("Arduino"))
					{
						Log.Informational("Connecting to " + DeviceInfo.Name);

						this.arduinoUsb = new UsbSerial(DeviceInfo);
						this.arduinoUsb.ConnectionEstablished += () =>
							Log.Informational("USB connection established.");

						this.arduino = new RemoteDevice(this.arduinoUsb);
						this.arduino.DeviceReady += () =>
						{
							Log.Informational("Device ready.");

							this.arduino.pinMode(13, PinMode.OUTPUT);    // Onboard LED.
							this.arduino.digitalWrite(13, PinState.HIGH);

							this.arduino.pinMode(0, PinMode.INPUT);      // PIR sensor (motion detection).
							MainPage.Instance.DigitalPinUpdated(0, this.arduino.digitalRead(0));

							this.arduino.pinMode(1, PinMode.OUTPUT);     // Relay.
							this.arduino.digitalWrite(1, 0);             // Relay set to 0

							this.arduino.pinMode("A0", PinMode.ANALOG); // Light sensor.
							MainPage.Instance.AnalogPinUpdated("A0", this.arduino.analogRead("A0"));

							this.sampleTimer = new Timer(this.SampleValues, null, 1000 - DateTime.Now.Millisecond, 1000);
						};

						this.arduino.AnalogPinUpdated += (pin, value) =>
						{
							MainPage.Instance.AnalogPinUpdated(pin, value);
						};

						this.arduino.DigitalPinUpdated += (pin, value) =>
						{
							MainPage.Instance.DigitalPinUpdated(pin, value);
						};

						this.arduinoUsb.ConnectionFailed += message =>
						{
							Log.Error("USB connection failed: " + message);
						};

						this.arduinoUsb.ConnectionLost += message =>
						{
							Log.Error("USB connection lost: " + message);
						};

						this.arduinoUsb.begin(57600, SerialConfig.SERIAL_8N1);
						break;
					}
				}
			}
			catch (Exception ex)
			{
				Log.Emergency(ex);

				MessageDialog Dialog = new MessageDialog(ex.Message, "Error");
				await Dialog.ShowAsync();
			}
		}

		private void SampleValues(object State)
		{
			ushort A0 = this.arduino.analogRead("A0");
			PinState D0 = this.arduino.digitalRead(0);

			if (this.windowA0[0].HasValue)
			{
				this.sumA0 -= this.windowA0[0].Value;
				this.nrA0--;
			}

			Array.Copy(this.windowA0, 1, this.windowA0, 0, windowSize - 1);
			this.windowA0[windowSize - 1] = A0;
			this.sumA0 += A0;
			this.nrA0++;

			double AvgA0 = ((double)this.sumA0) / this.nrA0;
			int? v;

			if (this.nrA0 >= windowSize - 2)
			{
				int NrLt = 0;
				int NrGt = 0;

				foreach (int? Value in this.windowA0)
				{
					if (Value.HasValue)
					{
						if (Value.Value < AvgA0)
							NrLt++;
						else if (Value.Value > AvgA0)
							NrGt++;
					}
				}

				if (NrLt == 1 || NrGt == 1)
				{
					v = this.windowA0[spikePos];

					if (v.HasValue)
					{
						if ((NrLt == 1 && v.Value < AvgA0) || (NrGt == 1 && v.Value > AvgA0))
						{
							this.sumA0 -= v.Value;
							this.nrA0--;
							this.windowA0[spikePos] = null;

							AvgA0 = ((double)this.sumA0) / this.nrA0;

							Log.Informational("Spike removed.", new KeyValuePair<string, object>("A0", v.Value));
						}
					}
				}
			}

			int i, n;

			for (AvgA0 = i = n = 0; i < spikePos; i++)
			{
				if ((v = this.windowA0[i]).HasValue)
				{
					n++;
					AvgA0 += v.Value;
				}
			}

			if (n > 0)
			{
				AvgA0 /= n;
				double Light = (100.0 * AvgA0) / 1024;
				MainPage.Instance.LightUpdated(Light, 2, "%");
			}
		}

		/// <summary>
		/// Invoked when Navigation to a certain page fails
		/// </summary>
		/// <param name="sender">The Frame which failed navigation</param>
		/// <param name="e">Details about the navigation failure</param>
		void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
		{
			throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
		}

		/// <summary>
		/// Invoked when application execution is being suspended.  Application state is saved
		/// without knowing whether the application will be terminated or resumed with the contents
		/// of memory still intact.
		/// </summary>
		/// <param name="sender">The source of the suspend request.</param>
		/// <param name="e">Details about the suspend request.</param>
		private void OnSuspending(object sender, SuspendingEventArgs e)
		{
			var deferral = e.SuspendingOperation.GetDeferral();

			if (this.sampleTimer != null)
			{
				this.sampleTimer.Dispose();
				this.sampleTimer = null;
			}

			if (this.arduino != null)
			{
				this.arduino.digitalWrite(13, PinState.LOW);
				this.arduino.pinMode(13, PinMode.INPUT);     // Onboard LED.
				this.arduino.pinMode(1, PinMode.INPUT);      // Relay.

				this.arduino.Dispose();
				this.arduino = null;
			}

			if (this.arduinoUsb != null)
			{
				this.arduinoUsb.end();
				this.arduinoUsb.Dispose();
				this.arduinoUsb = null;
			}

			Log.Terminate();

			deferral.Complete();
		}
	}
}