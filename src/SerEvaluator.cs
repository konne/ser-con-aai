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
    using Grpc.Core;
    using Qlik.Sse;
    using Google.Protobuf;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using static Qlik.Sse.Connector;
    using NLog;
    using System.IO;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Q2gHelperQrs;
    using SerApi;
    using Hjson;
    #endregion

    public class SerEvaluator : ConnectorBase, IDisposable
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        #region Enumerator
        public enum SerFunction
        {
            CREATE = 1,
            STATUS = 2,
            ABORT = 3,
            START = 4
        }
        #endregion

        #region Properties & Variables
        private SerOnDemandConfig OnDemandConfig;
        private SessionManager sessionManager;
        #endregion

        #region Connstructor & Dispose
        public SerEvaluator(SerOnDemandConfig config)
        {
            OnDemandConfig = config;
            sessionManager = new SessionManager();
        }

        public void Dispose() { }
        #endregion

        #region Public functions
        public override Task<Capabilities> GetCapabilities(Empty request, ServerCallContext context)
        {
            try
            {
                logger.Info($"GetCapabilities was called");

                return Task.FromResult(new Capabilities
                {
                    PluginVersion = OnDemandConfig.AppVersion,
                    PluginIdentifier = OnDemandConfig.AppName,
                    AllowScript = false,
                    Functions =
                    {
                        new FunctionDefinition()
                        {
                             FunctionId = 1,
                             FunctionType = FunctionType.Scalar,
                             Name = SerFunction.CREATE.ToString(),
                             Params =
                             {
                                new Parameter() { Name = "TemplateFilename", DataType = DataType.String },
                                new Parameter() { Name = "OutputFormat", DataType = DataType.String },
                                new Parameter() { Name = "UseSelection", DataType = DataType.String },
                             },
                             ReturnType = DataType.String,
                        },
                        new FunctionDefinition()
                        {
                             FunctionId = 2,
                             FunctionType = FunctionType.Scalar,
                             Name = SerFunction.STATUS.ToString(),
                             ReturnType = DataType.String
                        },
                        new FunctionDefinition()
                        {
                            FunctionId = 3,
                            FunctionType = FunctionType.Scalar,
                            Name = SerFunction.ABORT.ToString(),
                            ReturnType = DataType.String
                        },
                        new FunctionDefinition()
                        {
                            FunctionId = 4,
                            FunctionType = FunctionType.Scalar,
                            Name = SerFunction.START.ToString(),
                            Params =
                            {
                                new Parameter() { Name = "Script", DataType = DataType.String }
                            },
                            ReturnType = DataType.String
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                logger.Error(ex, "GetCapabilities has errors");
                return null;
            }
        }

        public override async Task ExecuteFunction(IAsyncStreamReader<BundledRows> requestStream, IServerStreamWriter<BundledRows> responseStream, ServerCallContext context)
        {
            try
            {
                logger.Debug("ExecuteFunction was called");

                Thread.Sleep(200);
                var functionRequestHeaderStream = context.RequestHeaders.SingleOrDefault(header => header.Key == "qlik-functionrequestheader-bin");
                if (functionRequestHeaderStream == null)
                {
                    throw new Exception("ExecuteFunction called without Function Request Header in Request Headers.");
                }

                var functionRequestHeader = new FunctionRequestHeader();
                functionRequestHeader.MergeFrom(new CodedInputStream(functionRequestHeaderStream.ValueBytes));

                var commonHeader = context.RequestHeaders.ParseIMessageFirstOrDefault<CommonRequestHeader>();
                logger.Info($"request from user: {commonHeader.UserId} for AppId: {commonHeader.AppId}");
                var domainUser = new DomainUser(commonHeader.UserId);
                logger.Debug($"DomainUser: {domainUser.UserId.ToString()}\\{domainUser.UserDirectory.ToString()}");
                var userParameter = new UserParameter()
                {
                    AppId = commonHeader.AppId,
                    DomainUser = domainUser,
                };

                await context.WriteResponseHeadersAsync(new Metadata { { "qlik-cache", "no-store" } });
   
                logger.Debug($"Function id: {functionRequestHeader.FunctionId}");
                var row = GetParameter(requestStream);
                var result = new OnDemandResult() { Status = -1 };
                if (functionRequestHeader.FunctionId == (int)SerFunction.CREATE)
                {
                    userParameter.TemplateFileName = GetParameterValue(0, row);
                    logger.Debug($"Template path: {userParameter.TemplateFileName}");
                    userParameter.SaveFormats = GetParameterValue(1, row);
                    logger.Debug($"SaveFormat: {userParameter.SaveFormats}");
                    userParameter.UseUserSelesction = GetBoolean(GetParameterValue(2, row));
                    logger.Debug($"UseSelection: {userParameter.UseUserSelesction}");
                    result = CreateReport(userParameter);
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.STATUS)
                {
                    var session = sessionManager.GetExistsSession(new Uri(OnDemandConfig.QlikServer), domainUser);
                    if (session == null)
                        throw new Exception("No existing session found.");

                    userParameter.ConnectCookie = session.Cookie;
                    if (session.DownloadLink != null)
                        result = new OnDemandResult() { Status = 100, Link = session.DownloadLink };
                    else
                        result = Status(session.TaskId, userParameter);
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.ABORT)
                {
                    var session = sessionManager.GetExistsSession(new Uri(OnDemandConfig.QlikServer), domainUser);
                    if (session == null)
                        throw new Exception("No existing session found.");
                    var process = Process.GetProcessById(session.ProcessId);
                    process?.Kill();
                    Thread.Sleep(1000);
                    SoftDelete($"{OnDemandConfig.WorkingDir}\\{session.TaskId}");
                }
                else if (functionRequestHeader.FunctionId == (int)SerFunction.START)
                {
                    var jsonOrPath = GetParameterValue(0, row);
                    if(jsonOrPath.EndsWith(".hjson") || jsonOrPath.EndsWith(".json"))
                        jsonOrPath = File.ReadAllText(jsonOrPath);

                    var json = HjsonValue.Load(jsonOrPath);
                    var validateResult = JsonConvert.DeserializeObject<SerConfig>(json.ToString());
                    if (validateResult != null)
                    {
                        logger.Debug("Script is valid.");
                        result = CreateReport(userParameter, jsonOrPath);
                    }
                    else
                    {
                        throw new Exception("Json Script is invalid.");
                    }
                }
                else
                {
                    throw new Exception($"Unknown function id {functionRequestHeader.FunctionId}.");
                }

                logger.Debug($"Result: {result}");
                await responseStream.WriteAsync(GetResult(result));
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ExecuteFunction has errors");
                await responseStream.WriteAsync(GetResult(new OnDemandResult()
                {
                    Log = ex.ToString(),
                    Status = -1
                }));
            }
            finally
            {
                LogManager.Flush();
            }
        }
        #endregion

        #region Private Functions
        private OnDemandResult CreateReport(UserParameter parameter, string json = null)
        {
            try
            {
                var tplPath = parameter.TemplateFileName;
                if (!File.Exists(tplPath))
                    tplPath = Path.Combine(OnDemandConfig.TemplateFolder, tplPath);

                if (!File.Exists(tplPath))
                    throw new Exception($"Template path {tplPath} not exits.");

                //Template aus App exportieren
                //Copy Template
                var workDir = OnDemandConfig.WorkingDir;
                var taskId = Guid.NewGuid().ToString();
                logger.Debug($"New Task-ID: {taskId}");
                var currentWorkingDir = Path.Combine(workDir, taskId);
                logger.Debug($"TempFolder: {currentWorkingDir}");
                Directory.CreateDirectory(currentWorkingDir);
                var tplCopyPath = Path.Combine(currentWorkingDir, Path.GetFileName(tplPath));
                File.Copy(tplPath, tplCopyPath, true);

                //Get a session
                var session = sessionManager.GetSession(new Uri(OnDemandConfig.QlikServer), parameter.DomainUser,
                                                        OnDemandConfig.VirtualProxy, taskId);
                parameter.ConnectCookie = session.Cookie;

                //Save config for SER engine
                var savePath = Path.Combine(currentWorkingDir, "job.json");
                logger.Debug($"Save SER config file \"{savePath}\"");
                if (json == null)
                    json = GetNewSerConfig(tplCopyPath, parameter);
                File.WriteAllText(savePath, json);

                //Start SER Engine as Process
                logger.Debug($"Start Engine \"{currentWorkingDir}\"");
                var serProcess = new Process();
                serProcess.StartInfo.FileName = OnDemandConfig.SerEnginePath;
                serProcess.StartInfo.Arguments = $"--workdir \"{currentWorkingDir}\"";
                serProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                serProcess.Start();
                session.ProcessId = serProcess.Id;

                //wait for finish and upload
                var uploadThread = new Thread(() => Upload(taskId, currentWorkingDir, parameter))
                {
                    IsBackground = true
                };
                uploadThread.Start();

                return new OnDemandResult() { Status = 0 };
            }
            catch (Exception ex)
            {
                throw new Exception("The report could not be created.", ex);
            }
        }

        private string GetParameterValue(int index, Row row)
        {
            try
            {
                if (row == null || row?.Duals?.Count == 0 || index >= row?.Duals.Count)
                {
                    logger.Warn($"Parameter index {index} not found.");
                    return null;
                }

                return row.Duals[index].StrData;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Parameter index {index} not found with exception.");
                return null;
            }
        }

        private BundledRows GetResult(OnDemandResult result)
        {
            var resultBundle = new BundledRows();
            var resultRow = new Row();
            resultRow.Duals.Add(new Dual { StrData = JsonConvert.SerializeObject(result) });
            resultBundle.Rows.Add(resultRow);
            return resultBundle;
        }

        private string GetNewSerConfig(string templatePath, UserParameter parameter)
        {
            try
            {
                var task = new SerTask()
                {
                    General = new SerGeneral()
                    {
                        UseUserSelections = parameter.UseUserSelesction,
                    },
                    Template = new SerTemplate()
                    {
                        FileName = templatePath,
                        SaveFormats = parameter.SaveFormats,
                        ReportName = Path.GetFileNameWithoutExtension(templatePath),
                    },
                    Connection = new SerConnection()
                    {
                        App = parameter.AppId,
                        ConnectUri = $"{OnDemandConfig.QlikServer}/{OnDemandConfig.VirtualProxy.Path}",
                        VirtualProxyPath = OnDemandConfig.VirtualProxy.Path,
                        Credentials = new SerCredentials()
                        {
                            Type = QlikCredentialType.SESSION,
                            Key = parameter.ConnectCookie.Name,
                            Value = parameter.ConnectCookie.Value,
                        }
                    }
                };

                var appConfig = new SerConfig() { Tasks = new List<SerTask> { task } }; 
                return JsonConvert.SerializeObject(appConfig);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Config for SER-Engine not saved.");
                return null;
            }
        }

        private void Upload(string taskId, string currentWorkingDir, UserParameter parameter)
        {
            try
            {
                var status = 0;
                while (status != 100)
                {
                    Thread.Sleep(250);
                    var result = Status(taskId, parameter);
                    status = result.Status;
                    if (status == -1)
                        return;
                }

                //Rename file name
                var reportFile = GetReportFile(taskId);
                var renamePath = Path.Combine(Path.GetDirectoryName(reportFile), $"{OnDemandConfig.ReportName}.{parameter.SaveFormats}");
                File.Move(reportFile, renamePath);

                //Upload Shared Content
                var qlikHub = new QlikQrsHub(new Uri($"{OnDemandConfig.QlikServer}/{OnDemandConfig.VirtualProxy.Path}"), 
                                             parameter.ConnectCookie);
                var hubInfo = GetFirstUserReport(parameter.DomainUser, parameter.ConnectCookie);
                if (hubInfo == null)
                {
                    var createRequest = new HubCreateRequest()
                    {
                        Name = OnDemandConfig.ReportName,
                        Description = $"Created by SER OnDemand Connector.",
                        Data = GetContentData(renamePath),
                    };
                    qlikHub.CreateSharedContentAsync(createRequest).Wait();
                    logger.Debug($"upload new file {reportFile} - Create");
                }
                else
                {
                    var updateRequest = new HubUpdateRequest()
                    {
                        Info = hubInfo,
                        Data = GetContentData(renamePath),
                    };
                    qlikHub.UpdateSharedContentAsync(updateRequest).Wait();
                    logger.Debug($"upload new file {reportFile} - Update");
                }

                //Wait for Status Success 
                Thread.Sleep(1000);

                //Download Url
                hubInfo = GetFirstUserReport(parameter.DomainUser, parameter.ConnectCookie);
                if (hubInfo == null)
                    logger.Debug("No Document uploaded.");
                else
                {
                    var url = $"{OnDemandConfig.QlikServer}{hubInfo?.References.FirstOrDefault().ExternalPath}";
                    logger.Debug($"Set Download Url {url}");
                    var session = sessionManager.GetExistsSession(new Uri(OnDemandConfig.QlikServer), parameter.DomainUser);
                    session.DownloadLink = url;
                }

                //Delete job files after upload
                SoftDelete(currentWorkingDir);
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
        }

        private Row GetParameter(IAsyncStreamReader<BundledRows> requestStream)
        {
            try
            {
                if (requestStream.MoveNext().Result == false)
                    logger.Debug("The Request has no parameters.");

                return requestStream?.Current?.Rows?.FirstOrDefault() ?? null;
            }
            catch
            {
                return null;
            }
        }

        private bool GetBoolean(string value)
        {
            if (value.ToLowerInvariant() == "true")
                return true;
            else if (value.ToLowerInvariant() == "false")
                return false;
            else
                return Boolean.TryParse(value, out var boolResult);
        }

        private HubContentData GetContentData(string fullname)
        {
            var contentData = new HubContentData()
            {
                ContentType = $"application/{Path.GetExtension(fullname).Replace(".", "")}",
                ExternalPath = Path.GetFileName(fullname),
                FileData = File.ReadAllBytes(fullname),
            };

            return contentData;
        }

        private bool SoftDelete(string folder)
        {
            try
            {
                Directory.Delete(folder, true);
                logger.Debug($"work dir {folder} deleted.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"The Folder {folder} could not deleted.");
                return false;
            }
        }

        private OnDemandResult Status(string taskId, UserParameter parameter)
        {
            var status = GetStatus(taskId);
            logger.Debug($"Report status {status}");
            if (status == "SUCCESS")
                return new OnDemandResult() { Status = 100 };
            else if (status == "ABORT")
                return new OnDemandResult() { Status = 1 };
            else if (status == "ERROR")
            {
                logger.Error("No Report created.");
                return new OnDemandResult() { Status = -1 };
            }
            else
                return new OnDemandResult() { Status = 0 };
        }

        private string GetResultFile(string taskId)
        {
            var resultFolder = Path.Combine(OnDemandConfig.WorkingDir, taskId, "JobResults");
            if (Directory.Exists(resultFolder))
            {
                var resultFiles = new DirectoryInfo(resultFolder).GetFiles("*.json", SearchOption.TopDirectoryOnly).ToList();
                var sortFiles = resultFiles.OrderBy(f => f.LastWriteTime).Reverse();
                return sortFiles.FirstOrDefault().FullName;
            }

            return null;
        }

        private HubInfo GetFirstUserReport(DomainUser user, Cookie cookie)
        {
            var qlikHub = new QlikQrsHub(new Uri($"{ OnDemandConfig.QlikServer}/{OnDemandConfig.VirtualProxy.Path}"), cookie);
            var selectRequest = new HubSelectRequest()
            {
                Filter = HubSelectRequest.GetNameFilter(OnDemandConfig?.ReportName),
            };

            var results = qlikHub.GetSharedContentAsync(selectRequest).Result;
            var result = results?.Where(d => d?.Owner?.UserId == user?.UserId && 
                                     d?.Owner?.UserDirectory == user?.UserDirectory)?.FirstOrDefault() ?? null;
            return result;
        }

        private JObject GetJsonObject(string taskId = null)
        {
            var resultFile = GetResultFile(taskId);
            if (File.Exists(resultFile))
            {
                logger.Debug($"json file {resultFile} found.");
                var json = File.ReadAllText(resultFile);
                return JsonConvert.DeserializeObject<JObject>(json);
            }

            logger.Error($"json file {resultFile} not found.");
            return null;
        }

        private string GetReportFile(string taskId)
        {
            try
            {
                var jobject = GetJsonObject(taskId);
                var path = jobject["reports"].FirstOrDefault()["paths"].FirstOrDefault().Value<string>() ?? null;
                return path;
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }

        private string GetStatus(string taskId)
        {
            try
            {
                var jobject = GetJsonObject(taskId);
                return jobject?.Property("status")?.Value?.Value<string>() ?? null;
            }
            catch(Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }

        private string GetTaskId(string tempDir)
        {
            var jobject = GetJsonObject(tempDir);
            return jobject?.Property("taskId")?.Value.Value<string>() ?? null;
        }
        #endregion
    }
}