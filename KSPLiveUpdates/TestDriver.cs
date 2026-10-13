using System;
using System.Threading;
using System.Collections.Generic;
using System.Text;

namespace KSPLiveTelemetry
{
    public static class TestDriver
    {
        public static void main()
        {
            Console.WriteLine("about to send data to server");
            TelemetryUpdater updater = new TelemetryUpdater();
            UpdateData ud = new UpdateData();
            ud.BodyName = "Eeloo";
            ud.DVLastStage = 12120.39193;
            updater.writeShared(ud.JSON());
            // updater.sendHttpRequest();
            updater.launchHttpThread();
            Console.WriteLine("main thread sleeping for 2 seconds");
            Thread.Sleep(2000);
            Console.WriteLine("done");
        }
    }
}
