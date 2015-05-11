/*
 *  httpdSharp - Simple small HTTP server for static content
 * 
 *  Created by Pål Andreaasen (paal.andreassen@gmail.com)
 *  Licenced under LGPL
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.IO;
using System.Configuration;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace httpdSharp
{
    class Program
    {
        private static TcpListener _myListener;
        
        private static string AppVersion
        {
            get
            {
                Assembly myAssembly = Assembly.GetExecutingAssembly();
                AssemblyName myAssemblyName = myAssembly.GetName();
                return myAssemblyName.Version.ToString();
            }                
        }

        #region AppSettings
        private static int Port
        {
            get
            {
                return int.Parse(ConfigurationManager.AppSettings["Port"]);
            }
        }

        private static string WWWRoot
        {
            get
            {
                return ConfigurationManager.AppSettings["wwwroot"];
            }
        }

        private static string DefaultDocument
        {
            get
            {
                return ConfigurationManager.AppSettings["defaultdocument"];
            }
        }
        #endregion

        static void Main(string[] args)
        {
            try
            {                
                //start listing on the given port
                _myListener = new TcpListener(IPAddress.Any, Port);
                _myListener.Start();
               
                Console.WriteLine(string.Format("httpdSharp {0} by Pål Andreassen (paal.andreassen@gmail.com)", AppVersion));
                Console.WriteLine("http://httpdsharp.codeplex.com/\n");
                Console.WriteLine("Using wwwroot: " + WWWRoot);
                Console.WriteLine("Webserver running on port " + Port + ". Press ^C to stop.");                

                Thread th = new Thread(new ThreadStart(StartListen));
                th.Start();                
            }
            catch (Exception e)
            {
                Console.WriteLine("An unhandled exception occurred while starting :" + e.ToString());
            }
        }

        /// <summary>
        /// Main listener loop
        /// </summary>
        private static void StartListen()
        {                        
            //Loop until user aborts program
            while (true)
            {
                try
                {
                    //Wait for connection
                    Socket socket = _myListener.AcceptSocket();                    
                    if (socket.Connected)
                    {
                        //Start a new thread to handle this request, return to accept another connection
                        ThreadStart handler = delegate { HandleRequest(socket); };
                        new Thread(handler).Start();
                    }
                }
                catch (Exception ex)
                {
                    Debug(" unhandled exception: " + ex.ToString());
                }
            }
        }

        /// <summary>
        /// Handle the actual request in a separate thread
        /// </summary>
        /// <param name="socket"></param>
        private static void HandleRequest(Socket socket)
        {
            try
            {
                string url = "";
                string request = "";
                string httpVersion = "HTTP/1.0";
                string remoteClient = socket.RemoteEndPoint.ToString();

                Dictionary<string, string> headers = ReadHeaders(socket, out request);

                if (!request.ToUpper().StartsWith("GET"))
                {
                    Debug("Unsupported request " + request);
                    socket.Close();
                    return;
                }

                //Check for reverse proxy x-forwared-for header
                if (headers.ContainsKey("X-FORWARDED-FOR"))
                    remoteClient = headers["X-FORWARDED-FOR"].Trim();

                GetUrlAndVersion(request, out url, out httpVersion);
                if (url == "/")
                {
                    url += DefaultDocument;
                }

                //Map request to local file
                string localFile = MapToLocalFile(url);
                //ignora a query string
                localFile = localFile.Split('?')[0];

                if (!File.Exists(localFile))
                {
                    Send404NotFound(ref socket, remoteClient, httpVersion, url);
                    return;
                }

                //Set file to user
                string mimeType = GetMimeType(localFile);

                if (string.IsNullOrEmpty(mimeType))
                {
                    Send404NotFound(ref socket, remoteClient, httpVersion, url);
                    return;
                }

                byte[] bytes = File.ReadAllBytes(localFile);

                Debug(remoteClient + " " + url + " " + bytes.Length + " 200");

                SendHeader(httpVersion, mimeType, bytes.Length, " 200 OK", ref socket);
                SendToBrowser(bytes, ref socket);
            }
            catch (Exception ex)
            {
                Debug("Unexpectd error :" + ex);
            }
            finally
            {
                if (socket.Connected)
                    socket.Close();

                socket = null;
            }
        }

        private static void Send404NotFound(ref Socket socket, string remoteClient, string httpVersion, string url)
        {
            string errorMessage;
            errorMessage = "<html><body><H2>404 Not Found</H2></body></html>";
            SendHeader(httpVersion, "", errorMessage.Length, " 404 Not Found", ref socket);
            SendToBrowser(errorMessage, ref socket);
            Debug(remoteClient + " " + url + " 0 404");            
        }        

        private static string MapToLocalFile(string url)
        {
            return WWWRoot + url.Replace("/", "\\");   
        }

        private static void GetUrlAndVersion(string header, out string url, out string httpVersion)
        {
            httpVersion = header.Substring(header.Length - 8);

            url = header.Replace("GET ", "");
            url = url.Substring(0, url.Length - 9);
        }

        private static string GetMimeType(string localFile)
        {
            string mimeTypeDefs = ConfigurationManager.AppSettings["mimetypes"];

            string[] mimeTypes = mimeTypeDefs.Split(';');
            foreach (string mimeType in mimeTypes)
            {
                string fileExt = mimeType.Split('|')[0].Trim();
                string mime = mimeType.Split('|')[1].Trim();

                if (Path.GetExtension(localFile).ToLower() == fileExt.ToLower())
                    return mime;
            }

            return string.Empty;
        }

        /// <summary>
        /// Get a dictionary of all HTTP headers. The GET/HEAD/PUT/POST request is returned in the request string.
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="request"></param>
        /// <returns></returns>
        private static Dictionary<string, string> ReadHeaders(Socket socket, out string request)
        {            
            NetworkStream networkStream = new NetworkStream(socket);
            StreamReader sr = new StreamReader(networkStream);
            var headers = new Dictionary<string, string>();
            request = "";
            bool firstLine = true;
            while (true)
            {
                string header = sr.ReadLine();                              

                if (string.IsNullOrEmpty(header))
                    break;

                if (firstLine)
                {
                    request = header;
                    firstLine = false;                    
                    continue;
                }

                string key = header.Substring(0, header.IndexOf(":")).Trim().ToUpper();
                string value = header.Substring(header.IndexOf(":") + 1).Trim();

                headers[key] = value;
            }

            sr.Close();
            networkStream.Close();

            return headers;
        }

        /// <summary>
        /// Send HTTP header back to browser
        /// </summary>
        /// <param name="sHttpVersion"></param>
        /// <param name="sMIMEHeader"></param>
        /// <param name="iTotBytes"></param>
        /// <param name="sStatusCode"></param>
        /// <param name="mySocket"></param>
        private static void SendHeader(string httpVersion, string MIMEtype, int contentLength, string statusCode, ref Socket socket)
        {
            string buffer = "";

            // if Mime type is not provided set default to text/html
            if (string.IsNullOrEmpty(MIMEtype))
            {
                MIMEtype = "text/html";  // Default Mime Type is text/html
            }

            buffer = buffer + httpVersion + statusCode + "\r\n";
            buffer = buffer + "Server: httpdSharp\r\n";
            buffer = buffer + "Content-Type: " + MIMEtype + "\r\n";
            buffer = buffer + "Content-Length: " + contentLength + "\r\n\r\n";

            byte[] data = Encoding.ASCII.GetBytes(buffer);

            SendToBrowser(data, ref socket);
        }

        private static void SendToBrowser(String sData, ref Socket mySocket)
        {
            SendToBrowser(Encoding.ASCII.GetBytes(sData), ref mySocket);
        }

        /// <summary>
        /// Sends the data to the browser
        /// </summary>
        /// <param name="bSendData"></param>
        /// <param name="mySocket"></param>
        private static void SendToBrowser(byte[] data, ref Socket socket)
        {
            try
            {
                if (socket.Connected)
                {
                    if (socket.Send(data, data.Length, SocketFlags.None) == -1)
                        Console.WriteLine("Socket error cannot send data");                    
                }
                else
                    Console.WriteLine("Connection Dropped....");
            }
            catch (Exception e)
            {
                Console.WriteLine("Error Occurred : {0} ", e);
            }
        }

        /// <summary>
        /// Writes debug information to console and logfile. Method is syncronized between threads
        /// </summary>
        /// <param name="text"></param>
        [MethodImpl(MethodImplOptions.Synchronized)]
        private static void Debug(string text)
        {
            try
            {
                text = DateTime.UtcNow + " " + text;
                string logFile = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "httpdSharp.log");

                using (StreamWriter sw = File.AppendText(logFile))
                {
                    sw.WriteLine(text);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error writing to logfile: " + ex.Message);
            }
            Console.WriteLine(text);
        }

    }
}
