using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Web;

namespace ConsoleApp1
{
    class Program
    {
        static void Main(string[] args)
        {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string workingDirectory = Path.GetDirectoryName(exePath);
            Directory.SetCurrentDirectory(workingDirectory);

            string logFileName = $"log_{DateTime.Now:dd_MM_yyyy_HH_mm}.txt";

            if (!Directory.Exists("log"))
            {
                Directory.CreateDirectory("log");
            }

            if (args.Length > 0 && args[0].ToUpper() == "Y")
            {
                using (var connection = new SQLiteConnection("Data Source=yybackup.db"))
                {
                    connection.Open();
                    PerformBackup(logFileName, connection);
                }
            }
            else
            {
                int index = 1;
                while (index == 1)
                {
                    Console.Write("This is a backup shortcut. Do you want to resume (Y/N)? ");
                    string choice = Console.ReadLine();

                    if (choice == "Y")
                    {
                        using (var connection = new SQLiteConnection("Data Source=yybackup.db"))
                        {
                            connection.Open();
                            InitializeDatabase(connection);
                            PerformBackup(logFileName, connection);
                        }
                        index = 0;
                    }
                    else if (choice == "N")
                    {
                        break;
                    }
                    else
                    {
                        Console.WriteLine("Invalid input. Please enter 'Y' or 'N'.");
                    }
                }
            }
        }

        private static void InitializeDatabase(SQLiteConnection connection)
        {
            using (var cmd = new SQLiteCommand(connection))
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS yybackup (
                        Id INTEGER PRIMARY KEY,
                        fpath TEXT,
                        fName TEXT,
                        fsize TEXT,
                        flct TEXT
                    ) ;";
                cmd.ExecuteNonQuery();
            }
        }

        private static void PerformBackup(string logFileName, SQLiteConnection connection)
        {
            EnsureBackupTableExists(connection);

            Google.Apis.Drive.v3.DriveService service = Authorize();
            var rootFolder = CreateOrGetFolder(service, "root", "BackupRoot");

            string jsonFilePath = ".\\filePaths.json";
            List<jsonFileClass> filePathsInfo = ReadFilePathsFromJson(jsonFilePath);

            string companiesJsonPath = "companies.json";
            List<string> companyNames = GetCompanyNamesFromJson(companiesJsonPath);

            if (companyNames.Count == 1 && filePathsInfo.Count > 0)
            {
                string companyName = companyNames[0];
                var companyFolder = CreateOrGetFolder(service, rootFolder.Id, companyName);
                var currentTime = DateTime.Now.ToString("MM_dd_yy");

                var dayFolder = CreateOrGetFolder(service, companyFolder.Id, currentTime);
                var hourMinuteFolder = CreateOrGetNestedFolder(service, dayFolder.Id, DateTime.Now.ToString("HH.mm"));

                foreach (var filePathInfo in filePathsInfo)
                {

                    string[] files = Directory.GetFiles(
                        Path.GetDirectoryName(filePathInfo.FilePath),
                        Path.GetFileName(filePathInfo.FilePath));

                    foreach (string file in files)
                    {
                        Console.WriteLine(file);
                        if (string.IsNullOrEmpty(file))
                        {
                            Console.WriteLine("Skipping empty file path.");
                            continue;
                        }


                        FileInfo fileInfo = new FileInfo(file);
                        string fileName = fileInfo.Name;
                        string fileFullName = fileInfo.FullName;
                        DateTime lastChangeTime = fileInfo.LastWriteTime;

                        if (fileInfo.Exists)
                        {

                            string filePath = file;
                            string option = filePathInfo.Option;

                            FileData existingFileData = dataRead(connection, fileFullName);

                            if (existingFileData.FileSize == fileInfo.Length
                                    && existingFileData.LastChangeTime == lastChangeTime.ToString("yyyy-MM-dd HH:mm:ss")
                                    && existingFileData.fpath == fileFullName
                                    && option == "I")
                            {
                                string skipMessage = $"File '{fileName}' has not been modified. Skipping backup.";
                                Console.WriteLine(skipMessage);
                                WriteToLog(skipMessage, logFileName);
                                continue;
                            }
                            else
                            {
                                var uploadedFile = UploadFile(service, filePath, hourMinuteFolder.Id, lastChangeTime, logFileName);

                                string uploadMessage = $"File '{uploadedFile.Name}' was uploaded successfully. File ID: {uploadedFile.Id}";
                                WriteToLog(uploadMessage, logFileName);
                                Console.WriteLine(uploadMessage);

                                DataDelete(connection, fileFullName);

                                DataWrite(connection, fileName, fileFullName, fileInfo.Length, lastChangeTime);


                            }

                        }

                    }
                } 
            }
            else
            {
                Console.WriteLine("Error: There should be exactly one company name in the 'companies.json' file.");
            }
        }

        private class FileData
        {
            public string fpath { get; set; }
            public long FileSize { get; set; }
            public string LastChangeTime { get; set; }
        }

        private static FileData dataRead(SQLiteConnection connection, string fpathfull)
        {

            string fpath = null;
            string flct = null;
            long fSize = 0;

            using (var cmd = new SQLiteCommand(connection))
            {
                cmd.CommandText = string.Format("SELECT fpath, flct, fName, fSize FROM yybackup where fpath='{0}'", fpathfull);

                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        fpath = reader.GetString(0);
                        flct = reader.GetString(1);
                        fSize = Convert.ToInt32(reader.GetString(3));


                    }
                }
            }
            return new FileData { fpath = fpath, FileSize = fSize, LastChangeTime = flct };

        }

        private static void DataUpdate(SQLiteConnection connection, string fileName, long fileSize, DateTime lastChangeTime)
        {
            using (var cmd = new SQLiteCommand(connection))
            {
                cmd.CommandText = @"
            UPDATE yybackup SET fSize = @fileSize, flct = @lastChangeTime
            WHERE fPath = @fileName;
            ";
                cmd.Parameters.AddWithValue("@fileName", fileName);
                cmd.Parameters.AddWithValue("@fileSize", fileSize.ToString());
                cmd.Parameters.AddWithValue("@lastChangeTime", lastChangeTime.ToString("yyyy-MM-dd HH:mm:ss"));

                cmd.ExecuteNonQuery();
            }
        }

        private static bool DataCheckFileSize(SQLiteConnection connection, string fileName, long fileSize)
        {
            using (var cmd = new SQLiteCommand(connection))
            {
                cmd.CommandText = @"
            SELECT fSize FROM yybackup WHERE fPath = @fileName;
            ";
                cmd.Parameters.AddWithValue("@fileName", fileName);
                var result = cmd.ExecuteScalar();

                if (result != null && result != DBNull.Value)
                {
                    long storedFileSize = long.Parse(result.ToString());
                    return storedFileSize == fileSize;
                }

                return false;
            }
        }

        private static DateTime DataRead(SQLiteConnection connection, string fileName)
        {
            using (var cmd = new SQLiteCommand(connection))
            {
                cmd.CommandText = $@"
                    SELECT flct FROM yybackup WHERE fpath=@fileName;
                    ";
                cmd.Parameters.AddWithValue("@fileName", fileName);
                var result = cmd.ExecuteScalar();

                if (result != null && result != DBNull.Value)
                {
                    return DateTime.Parse(result.ToString());
                }

                return DateTime.MinValue;
            }
        }

        private static void DataWrite(SQLiteConnection connection, string fileName, string fileFullName, long fileSize, DateTime lastChangeTime)
        {
            using (var cmd = new SQLiteCommand(connection))
            {
                cmd.CommandText = @"
            INSERT INTO yybackup (fName, fPath, fSize, flct)
            VALUES (@fName, @fPath, @fSize, @flct)
        ";
                cmd.Parameters.AddWithValue("@fName", fileName);
                cmd.Parameters.AddWithValue("@fPath", fileFullName);
                cmd.Parameters.AddWithValue("@fSize", fileSize.ToString());
                cmd.Parameters.AddWithValue("@flct", lastChangeTime.ToString("yyyy-MM-dd HH:mm:ss"));

                cmd.ExecuteNonQuery();
            }
        }

        private static void DataDelete(SQLiteConnection connection, string fpath)
        {
            using (var cmd = new SQLiteCommand(connection))
            {
                cmd.CommandText = $@"
                    DELETE FROM yybackup WHERE fpath='{fpath}'
                    ";
                cmd.ExecuteNonQuery();
            }
        }

        private static void UpdateLastBackupAndChangeTime(SQLiteConnection connection, string fileName, DateTime backupTime, DateTime lastChangeTime)
        {
            using (var cmd = new SQLiteCommand(connection))
            {
                cmd.CommandText = $@"
                    UPDATE yybackup SET flct=@lastChangeTime -- Add flct
                    WHERE fpath='{fileName}' AND flct < @lastChangeTime;
                    ";
                cmd.Parameters.AddWithValue("@lastChangeTime", lastChangeTime.ToString());

                cmd.ExecuteNonQuery();
            }
        }

        private static Google.Apis.Drive.v3.DriveService Authorize()
        {
            DateTime localDate = DateTime.Now;
            string[] scopes = new string[] { Google.Apis.Drive.v3.DriveService.Scope.Drive, Google.Apis.Drive.v3.DriveService.Scope.DriveFile };
            var clientId = "";
            var clientSecret = "";

            var credential = GoogleWebAuthorizationBroker.AuthorizeAsync(new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            }, scopes, Environment.UserName, CancellationToken.None, new FileDataStore("Drive.Api.Auth.Store")).Result;

            Google.Apis.Drive.v3.DriveService service = new Google.Apis.Drive.v3.DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "CloudUpload",
            });
            service.HttpClient.Timeout = TimeSpan.FromMinutes(100);

            return service;
        }

        private class jsonFileClass
        {
            public string FilePath { get; set; }
            public string Option { get; set; }
        }

        private static List<jsonFileClass> ReadFilePathsFromJson(string jsonFilePath)
        {
            try
            {
                string jsonContent = File.ReadAllText(jsonFilePath);
                List<jsonFileClass> fileClasses = JsonConvert.DeserializeObject<List<jsonFileClass>>(jsonContent);

                if (fileClasses != null)
                {
                    return fileClasses;
                }
                else
                {
                    Console.WriteLine("No valid data found in the JSON file.");
                    return new List<jsonFileClass>();
                }
            }
            catch (Exception ex)
            {
                string errorMessage = $"Error reading JSON file: {ex.Message}";
                WriteToLog(errorMessage, "log.txt");
                Console.WriteLine(errorMessage);
                return new List<jsonFileClass>();
            }
        }

        private static List<string> GetCompanyNamesFromJson(string jsonFilePath)
        {
            try
            {
                string jsonContent = File.ReadAllText(jsonFilePath);
                dynamic jsonObject = JsonConvert.DeserializeObject(jsonContent);
                List<string> companyNames = new List<string>();

                if (jsonObject != null && jsonObject.Companies != null)
                {
                    foreach (var companyName in jsonObject.Companies)
                    {
                        companyNames.Add(companyName.ToString());
                    }
                }

                return companyNames;
            }
            catch (Exception ex)
            {
                string errorMessage = $"Error reading JSON file: {ex.Message}";
                WriteToLog(errorMessage, "log.txt");
                Console.WriteLine(errorMessage);
                return new List<string>();
            }
        }

        private static Google.Apis.Drive.v3.Data.File CreateOrGetFolder(Google.Apis.Drive.v3.DriveService service, string parentFolderId, string folderName)
        {
            var listRequest = service.Files.List();
            listRequest.Q = $"name='{folderName}' and mimeType='application/vnd.google-apps.folder' and '{parentFolderId}' in parents and trashed=false";
            var result = listRequest.Execute();
            var existingFolder = result.Files.FirstOrDefault();

            if (existingFolder != null)
            {
                return existingFolder;
            }
            else
            {
                var folderMetadata = new Google.Apis.Drive.v3.Data.File()
                {
                    Name = folderName,
                    MimeType = "application/vnd.google-apps.folder",
                    Parents = new List<string> { parentFolderId }
                };

                var request = service.Files.Create(folderMetadata);
                request.Fields = "id";
                var folder = request.Execute();
                return folder;
            }
        }

        private static Google.Apis.Drive.v3.Data.File CreateOrGetNestedFolder(Google.Apis.Drive.v3.DriveService service, string parentFolderId, string folderName)
        {
            var listRequest = service.Files.List();
            listRequest.Q = $"name='{folderName}' and mimeType='application/vnd.google-apps.folder' and '{parentFolderId}' in parents and trashed=false";
            var result = listRequest.Execute();
            var existingFolder = result.Files.FirstOrDefault();

            if (existingFolder != null)
            {
                return existingFolder;
            }
            else
            {
                var folderMetadata = new Google.Apis.Drive.v3.Data.File()
                {
                    Name = folderName,
                    MimeType = "application/vnd.google-apps.folder",
                    Parents = new List<string> { parentFolderId }
                };

                var request = service.Files.Create(folderMetadata);
                request.Fields = "id";
                var folder = request.Execute();
                return folder;
            }
        }

        private static Google.Apis.Drive.v3.Data.File UploadFile(Google.Apis.Drive.v3.DriveService service, string filePath, string folderId, DateTime localDate, string logFileName)
        {
            if (File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                var fileName = fileInfo.Name;

                var fileMetadata = new Google.Apis.Drive.v3.Data.File()
                {
                    Name = fileInfo.Name,
                    Parents = new List<string> { folderId }
                };

                byte[] byteArray = File.ReadAllBytes(filePath);
                MemoryStream stream = new MemoryStream(byteArray);

                var request = service.Files.Create(fileMetadata, stream, MimeMapping.GetMimeMapping(filePath));
                request.SupportsTeamDrives = true;

                request.ProgressChanged += (obj) => Request_ProgressChanged(obj, logFileName);
                request.ResponseReceived += (obj) => Request_ResponseReceived(obj, logFileName);

                request.Upload();
                return request.ResponseBody;
            }
            else
            {
                string errorMessage = "The file does not exist.";
                WriteToLog(errorMessage, logFileName);
                Console.WriteLine(errorMessage);
                return null;
            }
        }

        private static void Request_ProgressChanged(Google.Apis.Upload.IUploadProgress obj, string logFileName)
        {
            string progressMessage = $"Upload Status: {obj.Status} Bytes Sent: {obj.BytesSent}";
            WriteToLog(progressMessage, logFileName);
            Console.WriteLine(progressMessage);
        }

        private static void Request_ResponseReceived(Google.Apis.Drive.v3.Data.File obj, string logFileName)
        {
            if (obj != null)
            {
                string successMessage = $"File '{obj.Name}' was uploaded successfully. File ID: {obj.Id}";
                WriteToLog(successMessage, logFileName);
                Console.WriteLine(successMessage);
            }
        }

        private static void EnsureBackupTableExists(SQLiteConnection connection)
        {
            using (var cmd = new SQLiteCommand(connection))
            {
                cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS yybackup (
                Id INTEGER PRIMARY KEY,
                fpath TEXT,
                fName TEXT,
                fsize TEXT,
                flct TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_fName ON yybackup (fName);
        ";
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteToLog(string message, string logFileName)
        {
            try
            {
                using (StreamWriter writer = File.AppendText($"log/{logFileName}"))
                {
                    writer.WriteLine($"[{DateTime.Now}] {message}");
                }
            }
            catch (Exception ex)
            {
                string errorMessage = $"Error writing to log file: {ex.Message}";
                using (StreamWriter writer = File.AppendText($"log/{logFileName}"))
                {
                    writer.WriteLine($"[{DateTime.Now}] {errorMessage}");
                }
            }
        }
    }
}