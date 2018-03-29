﻿namespace SerConAai
{
    #region Usings
    using SerApi;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text;
    #endregion

    public class UserParameter
    {
        public string TemplateFileName { get; set; }
        public string SaveFormats { get; set; }
        public int UseUserSelesction { get; set; }
        public string AppId { get; set; }
        public DomainUser DomainUser { get; set; }
        public Cookie ConnectCookie { get; set; }
    }

    public class OnDemandResult
    {
        public int Status { get; set; }
        public string TaskId { get; set; }
        public string Link { get; set; }
        public string Log { get; set; }
    }
}
