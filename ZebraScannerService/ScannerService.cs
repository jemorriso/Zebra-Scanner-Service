using System;
using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

using System.Threading;
using System.ComponentModel;
using Motorola.Snapi;
using Motorola.Snapi.Constants.Enums;
using Motorola.Snapi.Constants;
using Motorola.Snapi.EventArguments;

using System.Text.RegularExpressions;

using Renci.SshNet;
using System.Reflection;
using log4net;
using log4net.Config;


namespace ZebraScannerService
{
	static class BarcodeExtension
	{
		public static string GetDescription(this Enum value)
		{
			var fi = value.GetType().GetField(value.ToString());

			var attributes = (DescriptionAttribute[])fi.GetCustomAttributes(typeof(DescriptionAttribute), false);

			if ((attributes != null) && (attributes.Length > 0))
				return attributes[0].Description;
			return value.ToString();
		}
	}
	// either needs to be declared outside ScannerService class, or inside and made public
	enum BarcodeType { nid, location, None };

	class ScannerService
	{
		// initialize timer to 5000 ms - user has 5 seconds to scan nid after scanning location/nid (add vs remove)
		private static System.Timers.Timer _timer = new System.Timers.Timer(5000) { AutoReset = false };

		private static Tuple<string, BarcodeType> prevScan;

		private static bool _scannerAttached;

		private static ConnectionInfo ConnInfo;

		private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		// for each notification define a colour, length of flash, and beep pattern
		private static Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>> notifications = new Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>>();

		public void Start()
		{
			_timer.Elapsed += OnTimerElapsed;
			// configure logging
			XmlConfigurator.Configure();

			//First open an instance of the CoreScanner driver.
			// Instance returns BarCodeScannerManager which has private CoreScanner driver. 
			// Then Open calls Open API (see Windows SDK) on the CoreScanner variable
			// I think it's called like this because BarcodeScannerManager is static (only 1 should exist)
			//bool openStatus = BarcodeScannerManager.Instance.Open();
			//if (!openStatus)
			//{
			//	Console.WriteLine("driver not correctly opened");
			//}
			if (!(BarcodeScannerManager.Instance.Open()))
			{
				_log.Fatal("Failed to open CoreScanner driver");
			}
			else
			{

				_log.Debug("CoreScanner driver instance opened");
			}

			//// Setup SSH connection info for remote inventory database access
			//ConnInfo = new ConnectionInfo("inventory", "tunet",
			//	new AuthenticationMethod[] {
			//		// Password based Authentication
			//		new PasswordAuthenticationMethod("tunet","Pa$$word")
			//	}
			//);

			// Setup SSH connection info for remote inventory database access
			ConnInfo = new ConnectionInfo("jmorrison", "jmorrison",
				new AuthenticationMethod[] {
					// Password based Authentication
					new PasswordAuthenticationMethod("jmorrison","Pa$$wordjm")
				}
			);

			_log.Debug("Added SSH connection info: jmorrison@jmorrison");

			//Get a List<IMotorolaBarcodeScanner> containing each connected scanner.
			// Calls GetScanner API and returns formatted list of scanners
			// scanners are datatype that have corescanner instance and info.
			//_scanners = BarcodeScannerManager.Instance.GetDevices();

			// Register for barcode events, by adding handler onBarcodeEvent to event BarcodeEvent.
			// BarcodeEvent is of type "event _ICoreScannerEvents_BarcodeEventEventHandler BarcodeEvent" (delegate defines signature)
			// onBarcodeData invokes DataReceived.
			// then call ExecCommand on CoreScanner internal

			// Pnp event occurs when scanner attaches/detaches from system
			BarcodeScannerManager.Instance.RegisterForEvents(EventType.Barcode, EventType.Pnp);

			BarcodeScannerManager.Instance.ScannerAttached += Instance_ScannerAttached;
			BarcodeScannerManager.Instance.ScannerDetached += Instance_ScannerDetached;

			// add subscriber onDataReceived
			BarcodeScannerManager.Instance.DataReceived += OnDataReceived;

			_log.Debug("Subscribed for events in BarcodeScannerManager: CCoreScanner.Barcode, CCoreScanner.Pnp");
			_log.Debug("Subscribed for events in Main: BarcodeScannerManager.ScannerAttached, BarcodeScannerManager.ScannerDetached");

			// can add device in use notification
			notifications.Add("barcodeFailure", Tuple.Create((LedMode?)LedMode.YellowOn, (LedMode?)LedMode.YellowOff, 300, (BeepPattern?)BeepPattern.OneLowLong));
			//notifications.Add("barcodeSuccess", Tuple.Create((LedMode?)LedMode.GreenOn, (LedMode?)LedMode.GreenOff, 150, (BeepPattern?)null));
			notifications.Add("databaseFailure", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 300, (BeepPattern?)BeepPattern.TwoLowLong));
			//notifications.Add("tryDatabase", Tuple.Create((LedMode?)LedMode.GreenOn, (LedMode?)LedMode.GreenOff, 150, (BeepPattern?)null));
			notifications.Add("tryDatabase", Tuple.Create((LedMode?)LedMode.GreenOn, (LedMode?)LedMode.GreenOff, 1000, (BeepPattern?)null));
			notifications.Add("genericScan", Tuple.Create((LedMode?)null, (LedMode?)null, 0, (BeepPattern?)BeepPattern.OneHighShort));
			notifications.Add("deviceReserved", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 300, (BeepPattern?)BeepPattern.ThreeHighShort));

			//invoke when program starts, method also invoked whenever a new scanner is connected
			//ConnectScanners();
			List<IMotorolaBarcodeScanner> scannerList = BarcodeScannerManager.Instance.GetDevices();
			Console.WriteLine("number of connected scanners: " + scannerList.Count);

			var connectedScanner = scannerList[0];
			Console.WriteLine("Scanner hostmode: " + connectedScanner.Info.UsbHostMode);
			Console.WriteLine("Scanner manufactured: " + connectedScanner.Info.DateOfManufacture);
			Console.WriteLine("Scanner PID: " + connectedScanner.Info.ProductId);
			Console.WriteLine("Scanner model #: " + connectedScanner.Info.ModelNumber);
			connectedScanner = scannerList[1];
			Console.WriteLine("Scanner hostmode: " + connectedScanner.Info.UsbHostMode);
			Console.WriteLine("Scanner manufactured: " + connectedScanner.Info.DateOfManufacture);
			Console.WriteLine("Scanner PID: " + connectedScanner.Info.ProductId);
			Console.WriteLine("Scanner model #: " + connectedScanner.Info.ModelNumber);

		}

		public void Stop() {
			BarcodeScannerManager.Instance.Close();
		}

		// *** not working ******
		private static void ConnectScanners()
		{
			foreach (var scanner in BarcodeScannerManager.Instance.GetDevices())
			{
				//scanner.Beeper.BeeperVolume = BeeperVolume.Low;
				//scanner.Actions.SetAttribute(138, 'B', 0); // sound beeper via attribute

				_log.Debug("Scanner id=" + scanner.Info.ScannerId + " set to USB OPOS mode");
				Console.WriteLine("host mode: " + scanner.Info.UsbHostMode);
				if (scanner.Info.UsbHostMode != HostMode.USB_OPOS)
				{
					Console.WriteLine("evidently, god hates me");
					scanner.SetHostMode(HostMode.USB_OPOS);

					Console.WriteLine("fuck you Jeremy");
					_log.Error("Failed to set scanner id=" + scanner.Info.ScannerId + " to USB OPOS mode. Retrying...");
					scanner.SetHostMode(HostMode.USB_OPOS);

					// not sure why this is here... perhaps for connection issues.
					// does this need to be improved? eg scanner A attached then scanner B becomes detached before this line gets executed. sleep forever?
					while (_scannerAttached == false)
					{
						Thread.Sleep(3000);
					}
				}


			}
		}

		private static void Instance_ScannerAttached(object sender, PnpEventArgs e)
		{
			_scannerAttached = true;
			// change the scanner mode if necessary
			ConnectScanners();

			// log scanner attached
			Console.WriteLine("Scanner id=" + e.ScannerId + " attached");
		}

		private static void Instance_ScannerDetached(object sender, PnpEventArgs e)
		{
			_scannerAttached = false;

			// improve logging
			Console.WriteLine("Scanner id=" + e.ScannerId + " detached");

			// can add persistent red if scanner becomes detached??? Or is it gone
		}

		private static void OnTimerElapsed(Object source, System.Timers.ElapsedEventArgs e)
		{
			// user didn't scan nid in time. Either prevNid or location is set; nullify both
			// case 9/10 : either defined -> both undefined.
			prevScan = null;
			Console.WriteLine("timer up!");
		}

		// handles data 
		private static void OnDataReceived(object sender, BarcodeScanEventArgs e)
		{
			SendNotification(e.ScannerId, notifications["tryDatabase"]);

			// is it possible to define a new type of barcode?? Could avoid regular expressions altogether

			_log.Debug("Barcode scan detected from scanner " + e.ScannerId + ": " + e.Data);
			Console.WriteLine("Barcode scan detected from scanner " + e.ScannerId + ": " + e.Data);

			//Console.WriteLine("Barcode type: " + e.BarcodeType.GetDescription());
			//Console.WriteLine("Data: " + e.Data);

			//// convert barcode to uppercase and strip any whitespace
			//string barcode = e.Data.ToUpper().Trim();

			//BarcodeType barcodeType = CheckBarcode(barcode);

			//if (barcodeType == BarcodeType.None)
			//{
			//	_log.Error("Barcode " + e.Data + " not recognized as location or NID");
			//	SendNotification(e.ScannerId, notifications["barcodeFailure"]);
			//}
			//else
			//{
			//	// if successful scan, then either stop timer or restart start it, so stop here.
			//	// stopping timer avoids potential race condition
			//	_timer.Stop();
			//	_log.Debug("Barcode " + barcode + " recognized as type " + barcodeType);

			//	// case 1: prevScan: null		current: nid1 		-> prevScan: nid1		timer: start	()
			//	// case 2: prevScan: null		current: location1	-> prevScan: location1	timer: start	()	 
			//	// case 3: prevScan: nid1		current: nid1		-> prevScan: null		timer: stop		(remove nid's location from database)				
			//	// case 4: prevScan: nid1		current: nid2		-> prevScan: nid2		timer: start	(overwrite previous nid with new prevScan nid)
			//	// case 5: prevScan: nid1		current: location1	-> prevScan: location1	timer: start	(nid scanned first - overwrite with location)
			//	// case 6: prevScan: location1	current: location1	-> prevScan: location1	timer: start	(overwrite same location)
			//	// case 7: prevScan: location1	current: location2 	-> prevScan: location2	timer: start	(overwrite new location)
			//	// case 8: prevScan: location1	current: nid1 		-> prevScan: null		timer: start	(update nid's location in database)

			//	// cases 1 and 2
			//	if (prevScan == null)
			//	{
			//		_timer.Start();
			//		prevScan = Tuple.Create(barcode, barcodeType);
			//	}
			//	// cases 5,6,7
			//	else if (barcodeType == BarcodeType.location)
			//	{
			//		_timer.Start();
			//		prevScan = Tuple.Create(barcode, barcodeType);
			//	}
			//	else 
			//	{
			//		if (prevScan.Item2 == BarcodeType.nid)
			//		{
			//			// case 3
			//			if (barcode.Equals(prevScan.Item1))
			//			{
			//				SendNotification(e.ScannerId, notifications["tryDatabase"]);
			//				UpdateDatabase(e.ScannerId, barcode);
			//				prevScan = null;
			//			}
			//			// case 4
			//			else
			//			{
			//				_timer.Start();
			//				prevScan = Tuple.Create(barcode, barcodeType);
			//			}
			//		}
			//		// case 8
			//		else
			//		{
			//			SendNotification(e.ScannerId, notifications["tryDatabase"]);
			//			location = prevScan.Item1;
			//			UpdateDatabase(e.ScannerId, barcode, location);
			//			prevScan = null;
			//		}
			//	}
			//}
		}

		// returns "nid" if barcode scanned is recognized as NID, and "location" if recognized as location
		public static BarcodeType CheckBarcode(string barcode)
		{
			string locationFormat = @"^P[NESW]\d{4}";
			string nidFormat = @"^\d{10}$";

			if (EvalRegex(locationFormat, barcode))
			{
				return BarcodeType.location;
			}
			else if (EvalRegex(nidFormat, barcode))
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

		public static void UpdateDatabase(uint scannerId, string nid, string location = null)
		{
			// Execute a (SHELL) Command that runs python script to update database
			using (var sshclient = new SshClient(ConnInfo))
			{
				sshclient.Connect();
				// C# will convert null string to empty in concatenation
				//using (var cmd = sshclient.CreateCommand("python3 /var/www/scripts/autoscan.py" + location + " " + nid))
				using (var cmd = sshclient.CreateCommand("python3 /home/jmorrison/Zebra-Scanner-Service/autoscan/dbtester.py " + nid + " " + location))
				{
					cmd.Execute();
					Console.WriteLine("Command>" + cmd.CommandText);
					Console.WriteLine("Return Value = {0}", cmd.ExitStatus);

					// user or comment exists on device, so can't take it
					if (cmd.ExitStatus == 3)
					{
						SendNotification(scannerId, notifications["deviceReserved"]);
						// log
					}
					// could not connect to database, or could not commit to database, or something unexpected has occurred
					else if (cmd.ExitStatus == 1 || cmd.ExitStatus == 2 || cmd.ExitStatus > 0)
					{
						// send notification from here so it's faster
						SendNotification(scannerId, notifications["databaseFailure"]);
						if (location != null)
						{
							_log.Fatal("Error connecting to database or updating database with location=" + location + ", NID=" + nid);
						}
						else
						{
							_log.Fatal("Error connecting to database or removing NID=" + nid + " from database");
						}
					}
					else
					{
						// **** fix this to ensure actually successful database update from autoscan.py
						if (location != null)
						{
							_log.Debug("Successfully updated database with location=" + location + ", NID=" + nid);
						}
						else
						{
							_log.Debug("Successfully removed NID=" + nid + " from its location. Device is still in database, without a location");
						}
					}
				}
				sshclient.Disconnect();
			}
		}

		public static void SendNotification(uint scannerId, Tuple<LedMode?, LedMode?, int, BeepPattern?> notificationParams)
		{
			//IMotorolaBarcodeScanner scanner = GetScannerFromId(scannerId);
			IMotorolaBarcodeScanner scanner = BarcodeScannerManager.Instance.GetDevices()[1];


			// sound beeper
			if (notificationParams.Item4 != null)
			{
				scanner.Actions.SoundBeeper((BeepPattern)notificationParams.Item4);
			}
			// flash LED
			if (notificationParams.Item1 != null && notificationParams.Item2 !=null)
			{
				scanner.Actions.ToggleLed((LedMode)notificationParams.Item1);
				//Thread.Sleep(notificationParams.Item3);
				//scanner.Actions.ToggleLed((LedMode)notificationParams.Item2);
			}
		}

		// ***** not working ****
		public static IMotorolaBarcodeScanner GetScannerFromId(uint scannerId)
		{
			foreach (var scanner in BarcodeScannerManager.Instance.GetDevices())
			{
				if (scanner.Info.ScannerId == scannerId)
				{
					return scanner;
				}
			}
			return null;
		}
	}
}
