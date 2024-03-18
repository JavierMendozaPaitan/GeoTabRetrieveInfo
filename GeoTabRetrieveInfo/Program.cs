using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Geotab.Checkmate;
using Geotab.Checkmate.ObjectModel;
using Geotab.Checkmate.ObjectModel.Engine;

namespace GeoTabRetrieveInfo
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            try 
            {
                var apiGenerated = await ValidateInputParametersAndAutentication(args);
                while (apiGenerated == null)
                {
                    Console.WriteLine("\nPress Enter to exit or any other key to try again!");
                    if (Console.ReadKey(true).Key == ConsoleKey.Enter)
                    {
                        return;
                    }
                    apiGenerated = await ValidateInputParametersAndAutentication(args);
                }

                Console.WriteLine(" Retrieving devices...");
                var devices = await apiGenerated.CallAsync<IList<Device>>("Get", typeof(Device)) ?? [];

                Console.WriteLine(" Generating tasks for devices info...");
                var tasks = new ConcurrentBag<(string? DeviceId, Task VehicleTask)>();
                var cancellationToken = new CancellationTokenSource();
                var token = cancellationToken.Token;
                foreach (var device in devices)
                {
                    var task = Task.Run(async () =>
                    {
                        while (!token.IsCancellationRequested)
                        {
                            var vehicleInfo = await RetrieveVehicleInfo(device, apiGenerated, token);
                            WriteInfoToCsv(vehicleInfo);
                            await Task.Delay(20000);
                        }
                    }, token);
                    tasks.Add((device.Id?.ToString(), task));
                    Console.WriteLine($"Retrieving Information Task for Device [{device.Name}]");
                }

                ReceiveRequestForCancelling(tasks, cancellationToken);
                Console.ReadLine();
            } 
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"Problems during execution: {ex.StackTrace}");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey(true);
            }
        }

        private static async Task<API?> ValidateInputParametersAndAutentication(string[] args)
        {
            if(args.Length == 0)
            {
                Console.WriteLine("Please, introduce the parameters in the next order: server database username password");
                Console.Write("Parameters: ");
                args = Console.ReadLine()?.Split((char)ConsoleKey.Spacebar, 4) ?? [];
            }

            if (args.Length != 4)
            {
                Console.WriteLine();
                Console.WriteLine("Command line parameters:");
                Console.WriteLine("dotnet run <server> <database> <username> <password>");
                Console.WriteLine();
                Console.WriteLine("Command line:        dotnet run server database username password inputfile");
                Console.WriteLine("server             - The server name (Example: my.geotab.com)");
                Console.WriteLine("database           - The database name (Example: G560)");
                Console.WriteLine("username           - The Geotab user name");
                Console.WriteLine("password           - The Geotab password");
                Console.WriteLine();
                return null;
            }

            // Process command line arguments
            string server = args[0];
            string database = args[1];
            string username = args[2];
            string password = args[3];          

            try
            {
                var api = new API(username, password, null, database, server);
                await api.AuthenticateAsync();
                Console.WriteLine("Successfully Authenticated");
                return api;
            }
            catch (InvalidUserException ex)
            {
                Console.WriteLine($"Invalid user: {ex}");
                return null;
            }
            catch (DbUnavailableException ex)
            {
                Console.WriteLine($"Database unavailable: {ex}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to authenticate user: {ex}");
                return null;
            }
        }
        private static async Task<VehicleInfo> RetrieveVehicleInfo(Device device, API api, CancellationToken token)
        {
            if(device == null || api == null)
            {
                return new();
            }

            var goDevice = device as GoDevice;            
                     
            var statusInfo = await api.CallAsync<List<DeviceStatusInfo>>("Get", typeof(DeviceStatusInfo), new
            {
                search = new DeviceStatusInfoSearch
                {
                    DeviceSearch = new DeviceSearch
                    {
                        Id = device.Id
                    }
                }
            }, token);

            var deviceStatusInfo = statusInfo?[0];

            // Retrieve the odometer status data
            var statusData = await api.CallAsync<List<StatusData>>("Get", typeof(StatusData), new
            {
                search = new StatusDataSearch
                {
                    DeviceSearch = new DeviceSearch(device.Id),
                    DiagnosticSearch = new DiagnosticSearch(KnownId.DiagnosticOdometerAdjustmentId),
                    FromDate = deviceStatusInfo?.DateTime
                }
            }, token);

            var odometerReading = statusData?[0].Data ?? 0;

            var vehicleInfo = new VehicleInfo
            {
                Id = device.Id?.ToString() ?? Guid.NewGuid().ToString(),
                Name = device.Name,
                VIN = goDevice?.VehicleIdentificationNumber,
                Timestamp = deviceStatusInfo?.DateTime,
                Odometer = Math.Round(RegionInfo.CurrentRegion.IsMetric ? odometerReading : Distance.ToImperial(odometerReading/1000), 0),
                Latitude = deviceStatusInfo?.Latitude ?? 0,
                Longitude = deviceStatusInfo?.Longitude ?? 0
            };

            return vehicleInfo;
        }
        private static async void WriteInfoToCsv(VehicleInfo info)
        {
            if(info == null)
            {
                return;
            }
            var directory = @$"{Environment.CurrentDirectory}/VehiclesInfoBackup";
            if(!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var path = Path.Combine(directory, info.Id);
            using (TextWriter writer = new StreamWriter(path, true))
            {                
                await writer.WriteLineAsync(info.InfoToString);
            }
        }
        private static async void ReceiveRequestForCancelling(ConcurrentBag<(string? DeviceId, Task VehicleTask)> tasks, CancellationTokenSource tokenSource)
        {
            Console.WriteLine("\nFor Cancelling, press 'c' or 'C'");
            var key = Console.ReadKey(true).KeyChar;
            if(key == 'c' || key == 'C')
            {
                tokenSource.Cancel();
                Console.WriteLine("Wait for tasks to be cancelled...");
            }
            try
            {
                var taskList = tasks.Select(x=> x.VehicleTask).ToList();
                await Task.WhenAll(taskList).ContinueWith(x => 
                {
                    Console.WriteLine("Tasks Cancelled!\nPress enter to exit...");
                });
            }
            catch (OperationCanceledException ex)
            {
                Console.WriteLine($"\n{nameof(OperationCanceledException)} thrown: {ex.StackTrace}\n");
            }
            finally
            {
                tokenSource.Dispose();
            }
        }
    } 
}
