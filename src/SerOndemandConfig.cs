﻿#region License
/*
Copyright (c) 2018 Konrad Mattheis und Martin Berthold
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */
#endregion

namespace SerConAai
{
    #region Usings
    using Microsoft.Extensions.PlatformAbstractions;
    using Newtonsoft.Json;
    using SerApi;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    #endregion

    public class SerOnDemandConfig
    {
        public string WorkingDir { get; set; }
        public string SerEnginePath { get; set; }
        public string DeliveryToolPath { get; set; }
        public int BindingPort { get; set; } = 50059;
        public string BindingHost { get; set; } = "localhost";
        public SerConnection Connection { get; set; }

        public string Framework { get; private set; } = RuntimeInformation.FrameworkDescription;
        public string OS { get; private set; } = RuntimeInformation.OSDescription;
        public string Architecture { get; private set; } = RuntimeInformation.OSArchitecture.ToString();
        public string AppVersion { get; private set; } = PlatformServices.Default.Application.ApplicationVersion;
        public string AppName { get; private set; } = PlatformServices.Default.Application.ApplicationName;

        [JsonIgnore]
        public string Fullname
        {
            get => $"{AppName} {AppVersion.ToString()}";
        }

        public string GetCertPath()
        {
            if (String.IsNullOrEmpty(Connection?.Credentials?.Cert))
                return null;

            if (File.Exists(Connection?.Credentials?.Cert))
                return Connection.Credentials.Cert;

            return Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, Connection?.Credentials?.Cert);
        }
    }
}
