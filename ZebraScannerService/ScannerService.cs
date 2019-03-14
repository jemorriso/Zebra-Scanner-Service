using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Motorola.Snapi;
using Motorola.Snapi.Constants.Enums;
using Motorola.Snapi.Constants;
using Motorola.Snapi.EventArguments;

using Renci.SshNet;

using log4net;
using log4net.Config;
using System.Reflection;

namespace ZebraScannerService
{
	// class handles current scanner info related to inventory
	public class ScannerInfo
	{
		public IMotorolaBarcodeScanner scanner;
		public int prefix;
		public ScannerTimer timer;
		public Tuple<string, BarcodeType> prevScan;
	}

	// timer class accepts scanner id and led mode so event handler has access to these fields
	public class ScannerTimer : System.Timers.Timer
	{
		public int scannerId;
		// needed for ledtimer, but not scantimer
		public LedMode? ledOff;
	}

	public enum BarcodeType { nid, location, None };

	class ScannerService
	{
		private static ConnectionInfo ConnInfo;
		private static ILog _log;
		private static SshClient sshclient;

		private static Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>> notifications;
		private static Dictionary<int, int> prefixes;
		private static Dictionary<int, ScannerInfo> scanners;

		// set behaviour of scanners
		public static void ConfigureScanners()
		{
			int asciiPrefix = 0x61;

			// no way to tell which scanner disconnects so reset everything whenever any scanner disconnects
			// drawback:  events in progress are left incomplete
			// ***** need to test this
			prefixes = new Dictionary<int, int>();
			scanners = new Dictionary<int, ScannerInfo>();

			List<IMotorolaBarcodeScanner> deviceList = BarcodeScannerManager.Instance.GetDevices();
			// Ensure scanners are attached
			if (deviceList.Count == 0)
			{
				_log.Error("No scanners are attached.");

				_log.Error("No scanners are attached.");
				return;
			}
			// want: multipoint-point, low volume, beep / flash green on good decode, mode IBMHID, assign prefix
			foreach (var scanner in deviceList)
			{
				// if cradle
				if (scanner.Info.ModelNumber == "CR0078-SC10007WR")
				{
					Console.WriteLine("setting hostmode IBMHID");
					scanner.SetHostMode(HostMode.USB_IBMHID);

					// Set cradle to multipoint-to-point mode, so that up to 3 scanners can be linked to it.
					scanner.Actions.StoreIBMAttribute(538, DataType.Bool, false);
				}
				// if scanner
				else
				{
					// Set beeper volume low
					scanner.Actions.StoreIBMAttribute(140, DataType.Byte, BeeperVolume.Low);
					// Set beeper tone medium
					scanner.Actions.StoreIBMAttribute(145, DataType.Byte, 1);

					// Set beep on BarcodeScanEvent - LED also flashes green (unable to change)
					scanner.Actions.StoreIBMAttribute(56, DataType.Bool, true);
					// Disable laser flashing on BarcodeScanEvent
					scanner.Actions.StoreIBMAttribute(859, DataType.Byte, 0);

					// Enable barcode prefix
					scanner.Actions.StoreIBMAttribute(235, DataType.Byte, 4);
					scanner.Actions.StoreIBMAttribute(99, DataType.Array, 1);
					// assign letter prefix for identification
					scanner.Actions.StoreIBMAttribute(105, DataType.Array, asciiPrefix);

					prefixes.Add(asciiPrefix, scanner.Info.ScannerId);
					scanners.Add(
						scanner.Info.ScannerId,
						new ScannerInfo
						{
							scanner = scanner,
							prefix = asciiPrefix,
							timer = null,
							prevScan = null
						}
					);
					asciiPrefix++;
				}
			}
		}

		public void Start()
		{
			// Setup logging
			_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
			XmlConfigurator.Configure();

			if (!(BarcodeScannerManager.Instance.Open()))
			{
				_log.Fatal("Failed to open CoreScanner driver");
			}
			else
			{
				_log.Debug("CoreScanner driver instance opened");
			}

			//// Setup SSH connection info for remote inventory database access
			//ConnInfo = new ConnectionInfo("jmorrison", "jmorrison",
			//	new AuthenticationMethod[] {
			//		// Password based Authentication
			//		new PasswordAuthenticationMethod("jmorrison","Pa$$wordjm")
			//	}
			//);
			//_log.Debug("Added SSH connection info: jmorrison@jmorrison");

			//Setup SSH connection info for remote inventory database access

		   ConnInfo = new ConnectionInfo("inventory", "tunet",
			   new AuthenticationMethod[] {
					// Password based Authentication
					new PasswordAuthenticationMethod("tunet","tunet")
			   }
		   );

			//sshclient = new SshClient(ConnInfo);
			sshclient = new SshClient(ConnInfo);
			CheckSSHConnection();
			_log.Debug("Added SSH connection info: tunet@inventory");

			BarcodeScannerManager.Instance.RegisterForEvents(EventType.Barcode, EventType.Pnp);
			BarcodeScannerManager.Instance.DataReceived += OnDataReceived;
			BarcodeScannerManager.Instance.ScannerAttached += OnScannerAttached;
			BarcodeScannerManager.Instance.ScannerDetached += OnScannerDetached;
			_log.Debug("Subscribed for events in BarcodeScannerManager: CCoreScanner.Barcode, CCoreScanner.Pnp");
			_log.Debug("Subscribed for events in Main: BarcodeScannerManager.ScannerAttached, BarcodeScannerManager.ScannerDetached");

			// Add notification beep flash / sequences to notifications dictionary
			notifications = new Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>>
			{
				{ "tryDatabase", Tuple.Create((LedMode?)LedMode.GreenOn, (LedMode?)LedMode.GreenOff, 1500, (BeepPattern?)null) },
				{ "timerUp", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 50, (BeepPattern?)BeepPattern.TwoLowShort) },
				{ "barcodeFailure", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 100, (BeepPattern?)BeepPattern.OneLowLong) },
				{ "databaseFailure", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 300, (BeepPattern?)BeepPattern.ThreeLowLong) }
			};
			ConfigureScanners();
		}

		public void Stop()
		{
			_log.Debug("Zebra Scanner Service stopped");
			sshclient.Disconnect();
			BarcodeScannerManager.Instance.Close();
		}
		// PnpEventArgs: there is no way of telling which scanner attached / detached
		private static void OnScannerAttached(object sender, PnpEventArgs e)
		{
			_log.Debug("Scanner attached");
			// every time scanner is attached, reconfigure all scanners, because there is no guarantee that scanner that has
			// previously detached has the same id when it reattaches, and we don't have the id of the scanner that has attached.
			ConfigureScanners();
		}

		private static void OnScannerDetached(object sender, PnpEventArgs e)
		{
			_log.Debug("Scanner detached");
		}

		private static void OnScanTimerElapsed(Object source, System.Timers.ElapsedEventArgs e)
		{
			// user didn't scan nid in time. 
			// case 9/10 : prevScan defined -> undefined
			Console.WriteLine("timer up!");
			_log.Error("Timed out waiting for barcode scan event");

			SendNotification(((ScannerTimer)source).scannerId, notifications["timerUp"]);
			scanners[((ScannerTimer)source).scannerId].prevScan = null;
		}

		// Handles LED timer elapsed event by getting LED colour that was toggled on when LED timer was created, and turning that colour off.
		private static void OnLedTimerElapsed(Object source, System.Timers.ElapsedEventArgs e)
		{
			LedMode ledOff = (LedMode)((ScannerTimer)source).ledOff;
			IMotorolaBarcodeScanner scanner = scanners[((ScannerTimer)source).scannerId].scanner;

			scanner.Actions.ToggleLed(ledOff);
		}

		private static void OnDataReceived(object sender, BarcodeScanEventArgs e)
		{  
			//Console.WriteLine("Barcode scan detected from scanner id=" + e.ScannerId + ": " + e.Data);
			_log.Debug("Barcode scan detected from scanner id=" + e.ScannerId + ": " + e.Data);

			// get prefix identifier and convert from char to int
			int prefix = Convert.ToInt32(e.Data[0]);
			int scannerId = prefixes[prefix];

			// chop prefix off barcode, and convert to uppercase and strip any whitespace
			string barcode = e.Data.Substring(1).ToUpper().Trim();
			BarcodeType barcodeType = CheckBarcode(barcode);

			if (barcodeType == BarcodeType.None)
			{
				_log.Error("Barcode " + e.Data + " not recognized as location or NID");
				SendNotification(scannerId, notifications["barcodeFailure"]);
			}
			else
			{
				// if successful scan, then either stop timer or restart start it, so stop here.
				if (scanners[scannerId].timer != null)
				{
					scanners[scannerId].timer.Stop();
				}
				scanners[scannerId].timer = new ScannerTimer
				{
					Interval = 5000,
					AutoReset = false,
					scannerId = scannerId,
					ledOff = null
				};
				scanners[scannerId].timer.Elapsed += OnScanTimerElapsed;

				_log.Debug("Barcode " + barcode + " recognized as type " + barcodeType);
				Console.WriteLine("Barcode " + barcode + " recognized as type " + barcodeType);

				// case 1: prevScan: null		current: nid1 		-> prevScan: nid1		timer: start	()
				// case 2: prevScan: null		current: location1	-> prevScan: location1	timer: start	()	 
				// case 3: prevScan: nid1		current: nid1		-> prevScan: null		timer: stop		(remove nid's location from database)				
				// case 4: prevScan: nid1		current: nid2		-> prevScan: nid2		timer: start	(overwrite previous nid with new prevScan nid)
				// case 5: prevScan: nid1		current: location1	-> prevScan: location1	timer: start	(nid scanned first - overwrite with location)
				// case 6: prevScan: location1	current: location1	-> prevScan: location1	timer: start	(overwrite same location)
				// case 7: prevScan: location1	current: location2 	-> prevScan: location2	timer: start	(overwrite new location)
				// case 8: prevScan: location1	current: nid1 		-> prevScan: null		timer: stop		(update nid's location in database)

				// cases 1 and 2
				if (scanners[scannerId].prevScan == null)
				{
					scanners[scannerId].timer.Start();
					scanners[scannerId].prevScan = Tuple.Create(barcode, barcodeType);
				}
				// cases 5,6,7
				else if (barcodeType == BarcodeType.location)
				{
					scanners[scannerId].timer.Start();
					scanners[scannerId].prevScan = Tuple.Create(barcode, barcodeType);
				}
				else
				{
					if (scanners[scannerId].prevScan.Item2 == BarcodeType.nid)
					{
						// case 3
						if (barcode.Equals(scanners[scannerId].prevScan.Item1))
						{
							SendNotification(scannerId, notifications["tryDatabase"]);
							UpdateDatabase(scannerId, barcode);
							scanners[scannerId].prevScan = null;
						}
						// case 4
						else
						{
							scanners[scannerId].timer.Start();
							scanners[scannerId].prevScan = Tuple.Create(barcode, barcodeType);
						}
					}
					// case 8
					else
					{
						SendNotification(scannerId, notifications["tryDatabase"]);
						string location = scanners[scannerId].prevScan.Item1;
						UpdateDatabase(scannerId, barcode, location);
						scanners[scannerId].prevScan = null;
					}
				}
			}
		}

		// returns "nid" if barcode scanned is recognized as NID, and "location" if recognized as location
		public static BarcodeType CheckBarcode(string barcode)
		{
			string locationFormat = @"^P[NESW]\d{4}";
			string nidFormat = @"(\d|[A-F]){10}$";
			string ertFormat = @"\d{8}$";

			if (EvalRegex(locationFormat, barcode))
			{
				return BarcodeType.location;
			}
			// ert NID is just treated as regular NID by this program
			else if (EvalRegex(nidFormat, barcode) || EvalRegex(ertFormat, barcode))
			{
				return BarcodeType.nid;
			}
			else
			{
				return BarcodeType.None;
			}
		}

		public static Boolean EvalRegex(string rxStr, string matchStr)
		{
			Regex rx = new Regex(rxStr);
			Match match = rx.Match(matchStr);

			return match.Success;
		}

		public static void SendNotification(int scannerId, Tuple<LedMode?, LedMode?, int, BeepPattern?> notificationParams)
		{
			IMotorolaBarcodeScanner scanner = scanners[scannerId].scanner;

			// sound beeper
			if (notificationParams.Item4 != null)
			{
				scanner.Actions.SoundBeeper((BeepPattern)notificationParams.Item4);
			}
			// flash LED
			if (notificationParams.Item1 != null && notificationParams.Item2 != null)
			{
				scanner.Actions.ToggleLed((LedMode)notificationParams.Item1);
				// start timer, and when timer is up, event handler turns off LED
				var _ledTimer = new ScannerTimer
				{
					Interval = notificationParams.Item3,
					AutoReset = false,
					scannerId = scanner.Info.ScannerId,
					ledOff = (LedMode)notificationParams.Item2
				};
				_ledTimer.Elapsed += OnLedTimerElapsed;
				_ledTimer.Start();
			}
		}

		// ensure SSH tunnel is established. If can't connect log error and stop service
		public static bool CheckSSHConnection()
		{
			if (!sshclient.IsConnected)
			{
				try
				{
					sshclient.Connect();
				}
				catch (Renci.SshNet.Common.SshConnectionException e)
				{
					_log.Fatal("Error establishing connection to inventory server.");
					return false;
				}
			}
			return true;
		}

		public static void UpdateDatabase(int scannerId, string nid, string location = null)
		{
			if (!CheckSSHConnection())
			{
				SendNotification(scannerId, notifications["databaseFailure"]);
			}
			using (var cmd = sshclient.CreateCommand("python3 /home/tunet/jmorrison/autoscan.py " + nid + " " + location))
			{
				cmd.Execute();

				Console.WriteLine("Command>" + cmd.CommandText);
				Console.WriteLine("Return Value = {0}", cmd.ExitStatus);
				// user or comment exists on device, so can't take it
				//if (cmd.ExitStatus == 3)
				//{
				//	Console.WriteLine("failed to update db");
				//	//SendNotification(scannerId, notifications["deviceReserved"]);
				//	// log
				//}

				if (cmd.ExitStatus > 0)
				{
					SendNotification(scannerId, notifications["databaseFailure"]);
					// could not connect to database, or could not commit to database, or something unexpected has occurred
					if (cmd.ExitStatus == 1)
					{
						Console.WriteLine("failed to update db");
						_log.Fatal("Error connecting to database.");
					}
					else if (cmd.ExitStatus == 2 || cmd.ExitStatus > 0)
					{
						if (location != null)
						{
							_log.Fatal("Error updating database with location=" + location + ", NID=" + nid);
						}
						else
						{
							_log.Fatal("Error removing NID=" + nid + " location info from database");
						}
					}
				}
				else
				{
					if (location != null)
					{
						_log.Debug("Successfully updated database with location=" + location + ", NID=" + nid);
					}
					else
					{
						_log.Debug("Successfully removed location info for NID=" + nid);
					}
				}
			}
		}
	}
}
