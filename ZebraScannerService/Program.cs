using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Topshelf;

namespace ZebraScannerService
{
	class Program
	{
		static void Main(string[] args)
		{
			// setup Topshelf service
			var rc = HostFactory.Run(x =>                                
			{
				x.Service<ScannerService>(s =>                            
				{
					s.ConstructUsing(tc => new ScannerService());
					s.WhenStarted(tc => tc.Start());                      
					s.WhenStopped(tc => tc.Stop());                       
				});
				x.RunAsLocalSystem();                                    

				x.SetDescription("Manages handling of the bluetooth Zebra barcode scanner");
				x.SetDisplayName("Zebra Scanner Service");
				x.SetServiceName("Zebra Scanner Service");                               
			});                                                           

			var exitCode = (int)Convert.ChangeType(rc, rc.GetTypeCode());
			Environment.ExitCode = exitCode;
		}
	}
}
