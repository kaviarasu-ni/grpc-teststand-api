﻿using ExampleClient;
using Grpc.Core;
using NationalInstruments.TestStand.API.Grpc;
using NationalInstruments.TestStand.Grpc.Client.Utilities;
using NationalInstruments.TestStand.Grpc.Net.Client.OO;
using NationalInstruments.TestStand.UI.Grpc;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SLClient
{
    internal class Example
    {
        public enum ExecutionAction
        {
            Break,
            Resume,
            Terminate
        }

        private readonly object _dataLock = new();
        private int _busyCount = 0;

        // remember any event loop tasks we start, so we can wait for them to exit when cleaning up
        private List<Tuple<Task, System.Threading.CancellationTokenSource>> _eventLoops = new();

        private readonly Clients _clients = new();

        private bool _isConnected = false;
        private string _serverAddress;
        private string _nonSequentialProcessModelName = null;
        private string _executionStateDescriptionLabel;

        // remember some objects we create on the server for later use
        private EngineInstance _engine = null;
        private ExecutionInstance _activeExecution;
        private CancellationTokenSource _activeExecutionCancellationTokenSource;
        private readonly HashSet<string> _reportLocationsOnServer = new(StringComparer.OrdinalIgnoreCase);

        private int _valueForSelectedItemIndex = -1;
        private readonly ClientOptions _clientOptions;
        private bool _connectionIsSecured;

        private int _numberOfTestSocketsExecuting = 0;
        private readonly HashSet<string> _executionIdsToTrace = new();

        public Example(ClientOptions options)
        {
            _clientOptions = options;
            _serverAddress = "127.0.0.1";

            SetupChannelValidationCallback();
            AppendPortNumberToAddress();


        }

        private void SetupChannelValidationCallback()
        {
            CreateChannelHelper.ValidateGrpcChannel += (_, args) =>
            {
                try
                {
                    var instanceLifetimeClient = new InstanceLifetime.InstanceLifetimeClient(args.GrpcChannel);
                    instanceLifetimeClient.GetDefaultLifespan(new InstanceLifetime_GetDefaultLifespanRequest());
                }
                catch (Exception exception)
                {
                    string connectionType = args.UseHttps ? "secured (https)" : "not-secured (http)";
                    string errorMessage = FormattableString.Invariant($"ERROR: Failed to connect to server using a '{connectionType}' connection with the following error:\n");

                    if (exception is RpcException rpcException)
                    {
                        errorMessage += rpcException.Status.Detail;
                    }
                    else
                    {
                        errorMessage += exception.Message;
                    }
                    args.SetErrorMessage(errorMessage);
                }
            };
        }
        private void AppendPortNumberToAddress()
        {
            // _serverAddressTextBox.Text += ":" + _clientOptions.Port;
        }

        private void SetConnectionStatus(bool isConnected)
        {
            // Control values need to be set in the UI thread. Also, running in the
            // UI thread, removes the need to add a lock when updating _isConnected.
            //_connectionStatusDescriptionLabel.Invoke((Action)(() =>
            //{
            SetConnectionStatusOnUIThread(isConnected);
            //}));

            Console.WriteLine("Connected Status : {0}", isConnected);
        }

        private void SetConnectionStatusOnUIThread(bool isConnected)
        {
            // Debug.Assert(!_connectionStatusDescriptionLabel.InvokeRequired);

            if (_isConnected != isConnected)
            {
                _isConnected = isConnected;
                if (_isConnected)
                {
                    // _serverHeartbeatTimer.Start();
                }
                else
                {
                    // Stop heartbeat since we know we are no longer connected to the server
                    // _serverHeartbeatTimer.Stop();
                }

                string connectionStatusString, connectionStatusImageName, connectionType;
                if (_isConnected)
                {
                    connectionStatusString = Constants.ConnectionStatusConnected;
                    connectionStatusImageName = Constants.ConnectionStatusConnected;
                    connectionType = _connectionIsSecured ? Constants.SecureConnection : Constants.NotSecureConnection;
                }
                else
                {
                    connectionStatusString = Constants.ConnectionStatusDisconnected;
                    connectionStatusImageName = Constants.ConnectionStatusDisconnected;
                    connectionType = Constants.NotConnected;

                    // Reset the execution status
                    SetExecutionStatus(Constants.NotExecutedSequenceFile);

                    //_deleteGlobalButton.Enabled = false;
                    //_commitGlobalsToDiskButton.Enabled = false;
                    //_valueTextBox.Enabled = false;

                    RefreshStationGlobals();
                }

                //_enableTracingCheckBox.Enabled = _isConnected;
                //_serverAddressTextBox.ReadOnly = _isConnected;
                //_serverAddressTextBox.BackColor = _serverAddressPanel.BackColor; // Keep the panel's background color when TextBox is read-only.
                //_connectButton.Text = _isConnected ? "Disconnect" : "Connect";

                //_connectionTypePictureBox.Image = new Bitmap(_imageList[connectionType]);
                //_connectionStatusPictureBox.Image = new Bitmap(_imageList[connectionStatusImageName]);
                //_connectionStatusDescriptionLabel.Text = connectionStatusString;
            }
        }

        private async Task DisconnectAsync()
        {
            lock (_dataLock)
            {
                if (_activeExecutionCancellationTokenSource != null)
                {
                    _activeExecutionCancellationTokenSource.Cancel();
                    _activeExecutionCancellationTokenSource = null;
                }
                _activeExecution = null;
            }

            await UpdateExecutionOptionsStateAsync();
            SetExecutionStatus(Constants.NotExecutedSequenceFile);
            SetConnectionStatusOnUIThread(false);

            Cleanup();
        }

        // pass false to onlyIfNeeded if the server address might have changed
        private void Setup(bool onlyIfNeeded)
        {
            if (!onlyIfNeeded || !_clients.HasChannel)
            {
                Cleanup();

                if (_clients.OpenChannel(_serverAddress, _clientOptions, out _connectionIsSecured, out string connectionErrors))
                {
                    // the engine is used a lot, make sure we have a reference handy
                    GetEngineReference();

                    InitializeProcessModelInformation();

                    _eventLoops.Add(HandleUIMessagesAsync());

                    InitializeEnableTracingOption();

                    LogLine("Connection Succeeded.");
                    SetConnectionStatus(true);

                    // Add station globals to the list view
                    RefreshStationGlobals();
                }
                else
                {
                    LogLine(connectionErrors);
                    SetConnectionStatus(false);
                }
            }
        }

        private void Cleanup()
        {
            // exit all event streams
            foreach (var cancellationTokenSource in from item in _eventLoops select item.Item2) cancellationTokenSource.Cancel();

            // wait for all event loop threads to exit
            Task.WaitAll((from item in _eventLoops select item.Item1).ToArray(), 10000);

            _eventLoops = new List<Tuple<Task, System.Threading.CancellationTokenSource>>();

            // if a channel already exists, dispose it
            if (_clients.HasChannel)
            {
                // let the server know this connection and all its instance ids are no longer needed.
                // this also exits all event streams, but we did that earlier so that the simultaneous Clearing of all the 
                // object instance ids doesn't generate an error from an grpc call inside an event loop that hasn't finished exiting.
                try
                {
                    _clients.InstanceLifetimeClient?.Clear(new InstanceLifetime_ClearRequest { DiscardConnection = true });
                }
                catch { } // the connection might have already gone bad, so ignore exceptions

                Task.Run(async () => await _clients.ShutdownAsync()).Wait(); // call async task in thread pool thread so that Wait() can't prevent the continuation from completing 
            }

            _engine = null; // all instance ids are now invalid
        }

        private static string _errorResultStatusConstant;

        private Tuple<Task, System.Threading.CancellationTokenSource> HandleUIMessagesAsync()
        {
            bool demoEventMessages = false;

            if (String.IsNullOrEmpty(_errorResultStatusConstant))
                _errorResultStatusConstant = _clients.StepPropertiesClient.Get_ResultStatus_Error(new ConstantValueRequest()).ReturnValue;

            var cancellationTokenSource = new System.Threading.CancellationTokenSource();

            // get stream of UIMessage events
            var call = _clients.EngineClient.GetEvents_UIMessageEvent(new Engine_GetEvents_UIMessageEventRequest
            {
                Instance = _engine,
                DiscardMultipleEventsWithinPeriod = 0.0,
                ReplyTimeout = 20.0,
                TimeoutCancelsEvents = true
            }, null, null, cancellationTokenSource.Token);

            var uiMessageEventStream = call.ResponseStream;
            var threadCompletionSource = new TaskCompletionSource();

            // read the message stream from a separate thread. Otherwise the asynchronous message reading loop would block whenever the thread in which it is established
            // blocks in a synchronous call, including synchronous gRPC calls. Because some TestStand gRPC API calls can generate events that require replies before completing
            // the call, event loops should not be in a thread that might make non-async calls to the TestStand API, or any other calls that might block for an unbounded period.
            Task.Factory.StartNew(() =>
            {
                HandleUiEventsAsync(demoEventMessages, call, uiMessageEventStream, cancellationTokenSource.Token, threadCompletionSource).Wait();
            }, TaskCreationOptions.LongRunning);

            return new Tuple<Task, System.Threading.CancellationTokenSource>(threadCompletionSource.Task, cancellationTokenSource);
        }

        private async Task HandleUiEventsAsync(
            bool demoEventMessages,
            AsyncServerStreamingCall<Engine_GetEvents_UIMessageEventResponse> call,
            IAsyncStreamReader<Engine_GetEvents_UIMessageEventResponse> uiMessageEventStream,
            CancellationToken cancellationToken,
            TaskCompletionSource threadCompletionTokenSource)
        {
            const int IndentOffset = 4;
            const int StatusLength = 7;

            try
            {
                await foreach (var uiMessageEvent in uiMessageEventStream.ReadAllAsync())
                {
                    DateTime now = DateTime.Now;
                    UIMessageCodes uiMessageCode = UIMessageCodes.ReservedZero;
                    ExecutionInstance activeExecution;

                    lock (_dataLock)
                    {
                        activeExecution = _activeExecution;
                    }

                    if (demoEventMessages)
                    {
                        LogLine($"received msg id {uiMessageEvent.Msg.Id}  eventId: {uiMessageEvent.EventId}");
                    }

                    // Only process UI messages if there is an active execution.
                    if (activeExecution != null)
                    {
                        UIMessage_Get_ExecutionResponse response = await _clients.UiMessageClient.Get_ExecutionAsync(new UIMessage_Get_ExecutionRequest
                        {
                            Instance = uiMessageEvent.Msg
                        });
                        ExecutionInstance executionInstance = response.ReturnValue;
                        uiMessageCode = _clients.UiMessageClient.Get_Event(new UIMessage_Get_EventRequest { Instance = uiMessageEvent.Msg }).ReturnValue;

                        switch (uiMessageCode)
                        {
                            case UIMessageCodes.UimsgStartExecution:
                                {
                                    // Parallel and Batch process models create new executions for the test sockets. Those execution need to be traced.
                                    // Since the executions can take time to start and there is not enough information to determine execution hierarchy
                                    // (there is no parent execution property on an execution object), the first executions up to the total number of
                                    // test sockets expected will be treated as the socket executions for the process model.
                                    // This approach will not work in all cases. If one of the sockets starts a new execution before a new socket is 
                                    // started, the new execution will be treated as one of the sockets which is not correct.
                                    // More elaborated approaches can be made by looking at model data to determine the execution information,
                                    // but it will require more calls and it will be tied to a process model implementation.
                                    if (_executionIdsToTrace.Count < _numberOfTestSocketsExecuting)
                                    {
                                        _executionIdsToTrace.Add(executionInstance.Id);
                                    }
                                }
                                break;
                            case UIMessageCodes.UimsgEndExecution:
                                {
                                    bool traceExecution = _executionIdsToTrace.Contains(executionInstance.Id);
                                    if (traceExecution)
                                    {
                                        var executionId = (await _clients.ExecutionClient.Get_IdAsync(new Execution_Get_IdRequest { Instance = executionInstance })).ReturnValue;

                                        // Log the end of an execution only if tracing is enabled.
                                        //if (_enableTracingCheckBox.Checked)
                                        //{
                                        LogTraceMessage(System.FormattableString.Invariant($"Execution with id '{executionId}' is done running.") + Environment.NewLine);
                                        //}

                                        ReportInstance report = _clients.ExecutionClient.Get_Report(new Execution_Get_ReportRequest { Instance = executionInstance }).ReturnValue;
                                        string reportPath = _clients.ReportClient.Get_Location(new Report_Get_LocationRequest { Instance = report }).ReturnValue;
                                        if (!string.IsNullOrEmpty(reportPath))
                                        {
                                            _reportLocationsOnServer.Add(reportPath);
                                        }
                                    }
                                }
                                break;
                            case UIMessageCodes.UimsgTrace:
                                {
                                    bool traceExecution = _executionIdsToTrace.Contains(executionInstance.Id);
                                    int numberOfTestSocketsExecuting = _numberOfTestSocketsExecuting;

                                    // Only process the trace messages of the executions that we know of.
                                    if (traceExecution)
                                    {
                                        string message = string.Empty;

                                        var threadInstance = _clients.UiMessageClient.Get_Thread(new UIMessage_Get_ThreadRequest { Instance = uiMessageEvent.Msg }).ReturnValue;
                                        var sequenceContextInstance = _clients.ThreadClient.GetSequenceContext(new Thread_GetSequenceContextRequest { Instance = threadInstance, CallStackIndex = 0 }).ReturnValue;
                                        var sequenceContextPropertyObjectInstance = new PropertyObjectInstance { Id = sequenceContextInstance.Id };
                                        var previousStepIndex = _clients.SequenceContextClient.Get_PreviousStepIndex(new SequenceContext_Get_PreviousStepIndexRequest { Instance = sequenceContextInstance }).ReturnValue;

                                        if (previousStepIndex >= 0)
                                        {
                                            if (numberOfTestSocketsExecuting > 1)
                                            {
                                                int socketNumber = (int)_clients.PropertyObjectClient.GetValNumber(new PropertyObject_GetValNumberRequest
                                                {
                                                    Instance = sequenceContextPropertyObjectInstance,
                                                    LookupString = "Runstate.TestSockets.MyIndex",
                                                    Options = PropertyOptions.PropOptionNoOptions
                                                }).ReturnValue;

                                                // Make socket two characters long and left aligned it
                                                message = System.FormattableString.Invariant($"Socket {socketNumber,-2}  ");
                                            }

                                            var previousStepInstance = (await _clients.SequenceContextClient.Get_PreviousStepAsync(new SequenceContext_Get_PreviousStepRequest { Instance = sequenceContextInstance })).ReturnValue;
                                            var stepName = (await _clients.StepClient.Get_NameAsync(new Step_Get_NameRequest { Instance = previousStepInstance })).ReturnValue;
                                            var status = (await _clients.StepClient.Get_ResultStatusAsync(new Step_Get_ResultStatusRequest { Instance = previousStepInstance })).ReturnValue;

                                            // Make status 7 characters long and left aligned it.
                                            string statusFormatted = string.Format("{0,-" + StatusLength + "}", status);
                                            message += System.FormattableString.Invariant($"{statusFormatted}  Step {stepName}");

                                            if (status == _errorResultStatusConstant)
                                            {
                                                var stepObj = new PropertyObjectInstance { Id = previousStepInstance.Id }; // no need to call AsPropertyObject, just use the same Id and save a round trip
                                                var errorCode = (await _clients.PropertyObjectClient.GetValNumberAsync(new PropertyObject_GetValNumberRequest { Instance = stepObj, LookupString = "Result.Error.Code", Options = PropertyOptions.PropOptionNoOptions })).ReturnValue;
                                                var errorMessage = (await _clients.PropertyObjectClient.GetValStringAsync(new PropertyObject_GetValStringRequest { Instance = stepObj, LookupString = "Result.Error.Msg", Options = PropertyOptions.PropOptionNoOptions })).ReturnValue;

                                                // Indent the Code label below the Step label
                                                int codeStartingIndex = message.IndexOf("Step") + IndentOffset;
                                                string indentedCodeLabel = string.Format("\n{0," + codeStartingIndex + "}Code", "");

                                                message += System.FormattableString.Invariant($"{indentedCodeLabel} {errorCode}  Message {errorMessage}");
                                            }

                                            LogTraceMessage(message + Environment.NewLine);
                                        }
                                    }
                                }
                                break;
                        }
                    }

                    _ = _clients.EngineClient.ReplyToEvent_UIMessageEventAsync(new Engine_ReplyToEvent_UIMessageEventRequest { EventId = uiMessageEvent.EventId });

                    if (demoEventMessages)
                    {
                        var elapsed = DateTime.Now - now;
                        LogLine("UIMessage event: " + uiMessageCode.ToString() + ", Processing Time = " + elapsed.TotalSeconds.ToString());
                    }
                }

                LogLine("The UIMessage event stream exited without an error.");

            }
            catch (RpcException rpcException)
            {
                // When disconnecting from the server, the client cancels the event stream. This usually results in an exception with a status code of StatusCode.Cancelled, but other codes are possible.
                if (cancellationToken.IsCancellationRequested)
                {
                    LogLine("The UI message event stream has been cancelled.");
                }
                else
                {
                    LogLine("The UIMessage event stream exited with an error: " + rpcException.Message);
                }
            }
            catch (Exception exception)
            {
                LogLine("The UIMessage event stream exited with an error: " + exception.Message);
            }

            call.Dispose(); // cancels the call, in case we exited with an error
            threadCompletionTokenSource.SetResult();
        }

        private void GetEngineReference()
        {
            if (_engine == null)
            {
                _engine = _clients.EngineClient.Engine(new Engine_EngineRequest()).ReturnValue;

                // in case someone changes the default lifespan, always make the engine have an unlimited lifespan
                _clients.InstanceLifetimeClient.SetLifespan(new InstanceLifetime_SetLifespanRequest
                {
                    Value = new ObjectInstance() { Id = _engine.Id },
                    LifeSpan = _clients.InstanceLifetimeClient.Get_InfiniteLifetime(new InstanceLifetime_Get_InfiniteLifetimeRequest()).ReturnValue
                });
            }
        }

        private void InitializeProcessModelInformation()
        {
            // Initialize the process model MRU list
            PropertyObjectFileInstance configFile = _clients.EngineClient.GetEngineConfigFile(
                new Engine_GetEngineConfigFileRequest
                {
                    Instance = _engine,
                    ConfigFileType = PropertyObjectFileTypes.FileTypeGeneralEngineConfigFile
                }).ReturnValue;

            PropertyObjectInstance data = _clients.PropertyObjectFileClient.Get_Data(new PropertyObjectFile_Get_DataRequest { Instance = configFile }).ReturnValue;
            string mruList = _clients.PropertyObjectClient.GetValString(
                new PropertyObject_GetValStringRequest
                {
                    Instance = data,
                    LookupString = "ModelsMRUList",
                    Options = PropertyOptions.PropOptionNoOptions
                }).ReturnValue;
            string[] processModels = mruList.Split('|');

            // _stationModelComboBox.Items.Clear();
            // _stationModelComboBox.Items.AddRange(processModels);

            // Get the active process model and the number of test sockets
            StationOptionsInstance stationOptions = GetStationOptions();
            string modelPath = _clients.StationOptionsClient.Get_StationModelSequenceFilePath(
                new StationOptions_Get_StationModelSequenceFilePathRequest
                {
                    Instance = stationOptions
                }).ReturnValue;

            // _stationModelComboBox.Text = Path.GetFileName(modelPath);

            // Always dispose the tooltip. It will be recreated below if needed again.
            //_numTestSocketsNumericUpDownToolTip?.Dispose();
            //_numTestSocketsNumericUpDownToolTip = null;

            var _numTestSocketsNumericUpDown = GetMultipleUUTSettingsNumberOfTestSocketsOption();
            //if (_numTestSocketsNumericUpDown.Value == 0)
            //{
            //    _numTestSocketsNumericUpDown.Enabled = false;

            //    string tooltip = "This option is not available because the model options file is not found on the server.\n" +
            //        "Change a model option on the server to create the file.\n" +
            //        "Reconnect to the server to enable this option when using Batch and Parallel models.";
            //    _numTestSocketsNumericUpDownToolTip = new ToolTipEx(this, _numTestSocketsNumericUpDown, tooltip);
            //}
        }

        private void InitializeEnableTracingOption()
        {
            StationOptionsInstance stationOptions = GetStationOptions();
            //_enableTracingCheckBox.Checked = _clients.StationOptionsClient.Get_TracingEnabled(
            var _enableTracingCheckBoxChecked = _clients.StationOptionsClient.Get_TracingEnabled(
                new StationOptions_Get_TracingEnabledRequest
                {
                    Instance = stationOptions
                }).ReturnValue;

            UpdateTraceMessagesControls();
        }

        private void UpdateTraceMessagesControls()
        {
            //bool tracingIsEnabled = _enableTracingCheckBox.Checked;
            //_executionTraceMessagesLabel.Enabled = tracingIsEnabled;
            //_executionTraceMessagesTextBox.Enabled = tracingIsEnabled;
        }

        private StationOptionsInstance GetStationOptions()
        {
            return _clients.EngineClient.Get_StationOptions(new Engine_Get_StationOptionsRequest { Instance = _engine }).ReturnValue;
        }

        private int GetMultipleUUTSettingsNumberOfTestSocketsOption()
        {
            // Zero is not a valid value for number of test sockets. I cannot return -1 since the value is set on
            // _numTestSocketsNumericUpDown and that control does not accept negative values.
            int numberOfTestSockets = 0;

            PropertyObjectInstance modelOptions = GetProcessModelOptions();
            if (modelOptions != null)
            {
                // Get number of test sockets
                numberOfTestSockets = (int)_clients.PropertyObjectClient.GetValNumber(
                    new PropertyObject_GetValNumberRequest
                    {
                        Instance = modelOptions,
                        LookupString = Constants.NumberOfTestSocketsPropertyName,
                        Options = PropertyOptions.PropOptionNoOptions
                    }).ReturnValue;
            }

            return numberOfTestSockets;
        }

        private void SetMultipleUUTSettingsNumberOfTestSocketsOption(int numberOfSockets)
        {
            // Always get the model options from the server before setting the new number of test sockets.
            PropertyObjectInstance modelOptions = GetProcessModelOptions();
            if (modelOptions != null)
            {
                // Set the number of test sockets
                _clients.PropertyObjectClient.SetValNumber(
                    new PropertyObject_SetValNumberRequest
                    {
                        Instance = modelOptions,
                        LookupString = Constants.NumberOfTestSocketsPropertyName,
                        NewValue = numberOfSockets,
                        Options = PropertyOptions.PropOptionNoOptions
                    });

                // Persist the new value
                string modelOptionsFilePath = GetModelOptionsFilePath();
                _clients.PropertyObjectClient.Write(
                    new PropertyObject_WriteRequest
                    {
                        Instance = modelOptions,
                        PathString = modelOptionsFilePath,
                        ObjectName = Constants.ModelOptionsFileSectionName,
                        RWoptions = ReadWriteOptions.RwoptionEraseAll
                    });
            }
        }

        private PropertyObjectInstance GetProcessModelOptions()
        {
            const string ModelOptionsTypeName = "ModelOptions";

            string modelOptionsFilePath = GetModelOptionsFilePath();

            // Create object to store the model options
            PropertyObjectInstance modelOptions = _clients.EngineClient.NewPropertyObject(
                new Engine_NewPropertyObjectRequest
                {
                    Instance = _engine,
                    ValueType = PropertyValueTypes.PropValTypeNamedType,
                    AsArray = false,
                    TypeNameParam = ModelOptionsTypeName,
                    Options = PropertyOptions.PropOptionNoOptions
                }).ReturnValue;

            try
            {
                // Read the model options
                _clients.PropertyObjectClient.ReadEx(
                    new PropertyObject_ReadExRequest
                    {
                        Instance = modelOptions,
                        PathString = modelOptionsFilePath,
                        ObjectName = Constants.ModelOptionsFileSectionName,
                        RWoptions = ReadWriteOptions.RwoptionNoOptions,
                        HandlerType = TypeConflictHandlerTypes.ConflictHandlerUseGlobalType
                    });
            }
            catch (RpcException rpcException)
            {
                TSError errorCode = GetTSErrorCode(rpcException, out _);
                if (errorCode == TSError.TsErrFileWasNotFound || errorCode == TSError.TsErrUnableToOpenFile)
                {
                    // File does not exist on the server. Return a null object to let the caller know we
                    // cannot get the model options.
                    modelOptions = null;
                }
                else
                {
                    throw;
                }
            }

            return modelOptions;
        }

        private TSError GetTSErrorCode(RpcException rpcException, out string description)
        {
            description = null;

            string errorCodeString = rpcException.Trailers.GetValue("tserrorcode");
            if (!string.IsNullOrEmpty(errorCodeString))
            {
                if (int.TryParse(errorCodeString, out int errorCode))
                {
                    description = _clients.EngineClient.GetErrorString(new Engine_GetErrorStringRequest { Instance = _engine, ErrorCode = (TSError)errorCode }).ErrorString;
                    return (TSError)errorCode;
                }
            }

            return TSError.TsErrNoError;
        }

        private string GetModelOptionsFilePath()
        {
            const string ModelOptionsFilename = "TestStandModelModelOptions.ini";

            string modelOptionsFilePath = _clients.EngineClient.GetTestStandPath(
                new Engine_GetTestStandPathRequest
                {
                    Instance = _engine,
                    TestStandPath = TestStandPaths.TestStandPathConfig
                }).ReturnValue;
            modelOptionsFilePath = Path.Combine(modelOptionsFilePath, ModelOptionsFilename);

            return modelOptionsFilePath;
        }

        private void RefreshStationGlobals()
        {
            //_stationGlobalsListView.BeginUpdate();
            //_stationGlobalsListView.Items.Clear();

            if (_isConnected)
            {
                // Always refresh the station global by getting them directly from the server
                PropertyObjectInstance stationGlobals = _clients.EngineClient.Get_Globals(
                    new Engine_Get_GlobalsRequest
                    {
                        Instance = _engine
                    }).ReturnValue;
                int numberOfGlobals = _clients.PropertyObjectClient.GetNumSubProperties(
                    new PropertyObject_GetNumSubPropertiesRequest
                    {
                        Instance = stationGlobals,
                        LookupString = string.Empty
                    }).ReturnValue;

                List<object> globalVariables = new(numberOfGlobals);
                for (int index = 0; index < numberOfGlobals; index++)
                {
                    // Get the station global
                    PropertyObjectInstance global = _clients.PropertyObjectClient.GetNthSubProperty(
                        new PropertyObject_GetNthSubPropertyRequest
                        {
                            Instance = stationGlobals,
                            Index = index,
                            LookupString = string.Empty,
                            Options = PropertyOptions.PropOptionNoOptions
                        }).ReturnValue;

                    // Get name, value, and type information
                    string name = _clients.PropertyObjectClient.Get_Name(new PropertyObject_Get_NameRequest { Instance = global }).ReturnValue;
                    string value = _clients.PropertyObjectClient.GetValString(
                        new PropertyObject_GetValStringRequest
                        {
                            Instance = global,
                            LookupString = string.Empty,
                            Options = PropertyOptions.PropOptionCoerce
                        }).ReturnValue;
                    string displayType = _clients.PropertyObjectClient.GetTypeDisplayString(
                        new PropertyObject_GetTypeDisplayStringRequest
                        {
                            Instance = global,
                            LookupString = string.Empty,
                            Options = PropertyOptions.PropOptionNoOptions
                        }).ReturnValue;

                    // globalVariables.Add(new object(new string[] { name, value, displayType }));
                }

                if (numberOfGlobals > 0)
                {
                    //_stationGlobalsListView.Items.AddRange(globalVariables.ToArray());
                    //_stationGlobalsListView.SelectedIndices.Add(0);
                }
            }

            //_stationGlobalsListView.EndUpdate();

            //_stationGlobalsListView.Enabled = _isConnected;
            //_addGlobalButton.Enabled = _isConnected;
        }

        public async void OnConnectButtonClick(object sender, EventArgs e)
        {
            string actionDescription = _isConnected ? "Disconnect from server." : "Connect to server.";
            await TryActionAsync(async () =>
            {
                using (new AutoWaitCursor(this))
                {
                    if (_isConnected)
                    {
                        await DisconnectAsync();
                    }
                    else
                    {
                        Setup(false);
                    }
                }
            }, actionDescription);
        }

        private async Task TryActionAsync(Func<Task> action, string stringToLog)
        {
            bool logAction = !string.IsNullOrEmpty(stringToLog);
            if (logAction)
            {
                LogBold("Started: ");
                LogLine(stringToLog);
            }

            try
            {
                await action();
            }
            catch (Exception exception)
            {
                ReportException(exception);
            }
            finally
            {
                if (logAction)
                {
                    LogBold("Completed: ");
                    LogLine(stringToLog);
                }
            }
        }

        private void ReportException(Exception exception)
        {
            if (exception is RpcException rpcException)
            {
                // The grpc exceptions for some cases (like a bad server address) contain the stack trace in the Message, so using the Detail instead
                LogLine("gRPC EXCEPTION: " + rpcException.Status.Detail);

                TSError errorCode = GetTSErrorCode(rpcException, out string description);
                if (errorCode != TSError.TsErrNoError)
                {
                    LogFaded("\tError ");
                    Log(((int)errorCode).ToString());
                    LogFaded("  Message ");
                    LogLine(description);
                }

                if (rpcException.StatusCode == StatusCode.Unavailable)
                {
                    SetConnectionStatus(false);
                }
            }
            else
            {
                LogLine("EXCEPTION: " + exception.Message);
            }
        }

        private void OnProcessModelComboBoxSelectedIndexChanged(object sender, System.EventArgs e)
        {
            EnableOrDisableProcessModelOptionAndNumberOfTestSockets();
        }

        private void EnableOrDisableProcessModelOptionAndNumberOfTestSockets()
        {
            bool usingModel = true;
            //var selectedModel = (string)_processModelComboBox.SelectedItem;
            var selectedModel = "Use Station Model";
            bool usingStationModel = string.Compare(selectedModel, "Use Station Model", StringComparison.OrdinalIgnoreCase) == 0;

            _nonSequentialProcessModelName = null;
            if (usingStationModel)
            {
                //var stationModelFile = (string)_stationModelComboBox.SelectedItem;
                var stationModelFile = "BatchModel.seq";
                if (string.Compare(stationModelFile, "BatchModel.seq", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    _nonSequentialProcessModelName = "Batch";
                }
                else if (string.Compare(stationModelFile, "ParallelModel.seq", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    _nonSequentialProcessModelName = "Parallel";
                }
            }
            else if (string.Compare(selectedModel, "Batch", StringComparison.OrdinalIgnoreCase) == 0
                || string.Compare(selectedModel, "Parallel", StringComparison.OrdinalIgnoreCase) == 0)
            {
                _nonSequentialProcessModelName = selectedModel;

            }
            else
            {
                usingModel = string.Compare(selectedModel, "None", StringComparison.OrdinalIgnoreCase) != 0;
            }

            //_activeProcessModelLabel.Enabled = usingStationModel;
            //_stationModelComboBox.Enabled = usingStationModel;
            //_entryPointLabel.Enabled = usingModel;
            //_entryPointComboBox.Enabled = usingModel;

            //bool enableNumberOfTestSocketsOption = _nonSequentialProcessModelName != null && _numTestSocketsNumericUpDown.Value != 0;
            //_numberOfTestSocketsLabel.Enabled = enableNumberOfTestSocketsOption;
            //_numTestSocketsNumericUpDown.Enabled = enableNumberOfTestSocketsOption;
        }

        private void OnSequenceFileNameComboBoxSelectedIndexChanged(object sender, EventArgs e)
        {
            SetExecutionStatus(Constants.NotExecutedSequenceFile);
        }

        private void OnServerAddressTextBoxValidating(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // string[] addressAndPort = _serverAddressTextBox.Text.Split(new char[] { ':' });
            string[] addressAndPort = "127.0.0.1:5020".Split(new char[] { ':' });

            _serverAddress = addressAndPort[0];

            if (string.IsNullOrEmpty(_serverAddress) || addressAndPort.Length > 2)
            {
                //e.Cancel = true;
                //_errorProvider.SetError(_serverAddressTextBox, "Invalid format. Expected '<HostName/IPAddress>:<PortNumber>'.");
                //_connectButton.Enabled = false;
                //_connectionTypePictureBox.Visible = false;
            }
            else if (addressAndPort.Length == 1)
            {
                AppendPortNumberToAddress();
            }
            else  // There are two strings
            {
                if (string.IsNullOrEmpty(addressAndPort[1]))
                {
                    // Use previous port number since port is not specified.
                    // _serverAddressTextBox.Text = _serverAddress;
                    AppendPortNumberToAddress();
                }
                else
                {
                    // The port number specified in the address text box will override any existing port information
                    if (int.TryParse(addressAndPort[1], out int portNumber))
                    {
                        _clientOptions.Port = portNumber;
                    }
                    else
                    {
                        //e.Cancel = true;
                        //_errorProvider.SetError(_serverAddressTextBox, "Port number is not a valid number.");
                        //_connectButton.Enabled = false;
                        //_connectionTypePictureBox.Visible = false;
                    }
                }
            }
        }

        private void OnServerAddressTextBoxValidated(object sender, EventArgs e)
        {
            //_errorProvider.SetError(_serverAddressTextBox, string.Empty);
            //_connectButton.Enabled = true;
            //_connectionTypePictureBox.Visible = true;
        }

        public async void OnRunSequenceFileButtonClick(object sender, EventArgs e)
        {
            // Debug.Assert(_activeExecution == null);
            // Debug.Assert(_activeExecutionCancellationTokenSource == null);

            try
            {
                _reportLocationsOnServer.Clear();

                // Since only one execution can be run at a time, disable the run button when starting a new execution.
                // _runSequenceFileButton.Enabled = false;
                // _runSequenceFileButton.Text = Constants.RunningRemoteSequenceFile;

                await TryActionAsync(async () =>
                {
                    Setup(true);

                    // Don't run sequence if connection fails
                    if (!_isConnected)
                    {
                        return;
                    }

                    // get the sequence file to run
                    var sequenceFile = _clients.EngineClient.GetSequenceFileEx(new Engine_GetSequenceFileExRequest
                    {
                        Instance = _engine,
                        //SeqFilePath = _sequenceFileNameComboBox.Text,
                        SeqFilePath = "C:\\Users\\kaviarasu.selvarasu\\Desktop\\TestSequences\\LMS_Demo.seq",
                        GetSeqFileFlags = GetSeqFileOptions.GetSeqFileFindFile
                    }).ReturnValue;

                    SequenceFileInstance processModel = null;

                    try
                    {
                        processModel = GetSelectedProcessModel(out string modelName);
                        string sequenceName = GetSelectedSequenceName(processModel);

                        // The process models store the step results in a local variable called "ModelData". We need to get a 
                        // reference to ModelData to keep the local alive so we can get the step results after the execution ends.
                        PropertyObjectInstance modelData = await RunSequenceFileAsync(sequenceFile, sequenceName, processModel, modelName);

                        // While executing a sequence, the client can disconnect if user clicks on the disconnect button or the server
                        // can disconnect for some reason. If that is the case, there is nothing else to do.
                        if (_isConnected)
                        {
                            // Determine if an error occurred during the execution.
                            PropertyObjectInstance errorObject = _clients.ExecutionClient.Get_ErrorObject(new Execution_Get_ErrorObjectRequest { Instance = _activeExecution }).ReturnValue;
                            bool errorOccurred = _clients.PropertyObjectClient.GetValBoolean(
                                new PropertyObject_GetValBooleanRequest
                                {
                                    Instance = errorObject,
                                    LookupString = "Occurred",
                                    Options = PropertyOptions.PropOptionNoOptions
                                }).ReturnValue;

                            string resultStatus;
                            if (errorOccurred)
                            {
                                resultStatus = Constants.ExecutionStateError;
                            }
                            else
                            {
                                resultStatus = _clients.ExecutionClient.Get_ResultStatus(new Execution_Get_ResultStatusRequest { Instance = _activeExecution }).ReturnValue;
                            }

                            SetExecutionStatus(resultStatus);

                            LogExecutionResults(resultStatus, processModel, modelName, modelData);
                        }
                    }
                    finally
                    {
                        // While executing a sequence, the client can disconnect if user clicks on the disconnect button
                        // or the server disconnects. So, only release the sequences if client is still connected.
                        if (_isConnected)
                        {
                            // release file references we no longer need (files require explicit release)
                            if (processModel != null)
                            {
                                _clients.EngineClient.ReleaseSequenceFileEx(new Engine_ReleaseSequenceFileExRequest
                                {
                                    Instance = _engine,
                                    SequenceFileToRelease = processModel,
                                    Options = ReleaseSeqFileOptions.ReleaseSeqFileNoOptions
                                });
                            }

                            _clients.EngineClient.ReleaseSequenceFileEx(new Engine_ReleaseSequenceFileExRequest
                            {
                                Instance = _engine,
                                SequenceFileToRelease = sequenceFile,
                                Options = ReleaseSeqFileOptions.ReleaseSeqFileNoOptions
                            });
                        }
                    }
                }, "Run remote execution.");
            }
            finally
            {
                lock (_dataLock)
                {
                    // If _activeExecution is null, it means execution failed to run or server crashed in the middle
                    // of execution. So, reset state to not executed since any errors will appear in the log control.
                    if (_activeExecution == null)
                    {
                        SetExecutionStatus(Constants.NotExecutedSequenceFile);
                    }

                    _activeExecution = null;
                    _activeExecutionCancellationTokenSource = null;
                }

                _executionIdsToTrace.Clear();
                _numberOfTestSocketsExecuting = 0;

                //_runSequenceFileButton.Enabled = true;
                //_runSequenceFileButton.Text = Constants.RunRemoteSequenceFile;
            }
        }

        private SequenceFileInstance GetSelectedProcessModel(out string modelName)
        {
            SequenceFileInstance processModel = null;
            //modelName = (string)_processModelComboBox.SelectedItem;
            modelName = "Use Station Model";

            if (modelName != "None")
            {
                if (modelName == "Use Station Model")
                {
                    StationOptionsInstance stationOptions = GetStationOptions();
                    modelName = _clients.StationOptionsClient.Get_StationModelSequenceFilePath(
                        new StationOptions_Get_StationModelSequenceFilePathRequest
                        {
                            Instance = stationOptions
                        }).ReturnValue;
                }
                else
                {
                    modelName += "Model.seq";
                }

                processModel = _clients.EngineClient.GetSequenceFileEx(new Engine_GetSequenceFileExRequest
                {
                    Instance = _engine,
                    SeqFilePath = modelName,
                    GetSeqFileFlags = GetSeqFileOptions.GetSeqFileFindFile
                }).ReturnValue;
            }

            return processModel;
        }

        private string GetSelectedSequenceName(SequenceFileInstance processModel)
        {
            //return processModel == null ? "MainSequence" : _entryPointComboBox.Text;
            return "Single Pass";
        }

        private async Task<PropertyObjectInstance> RunSequenceFileAsync(
            SequenceFileInstance sequenceFile,
            string sequenceName,
            SequenceFileInstance processModel,
            string modelName)
        {
            var newExecutionRequest = new Engine_NewExecutionRequest
            {
                Instance = _engine,
                SequenceFileParam = sequenceFile,
                SequenceNameParam = sequenceName,
                BreakAtFirstStep = false,
                ExecutionTypeMaskParam = ExecutionTypeMask.ExecTypeMaskCloseWindowWhenDone
            };

            if (processModel != null)
            {
                newExecutionRequest.ProcessModelParam = processModel;
            }

            try
            {
                _numberOfTestSocketsExecuting = GetNumberOfTestSockets(processModel, modelName);
                _activeExecution = _clients.EngineClient.NewExecution(newExecutionRequest).ReturnValue;

                _executionIdsToTrace.Clear();
                _executionIdsToTrace.Add(_activeExecution.Id);
            }
            catch (RpcException rpcException)
            {
                TSError errorCode = GetTSErrorCode(rpcException, out string _);
                if (errorCode != TSError.TsErrNoError)
                {
                    // Some error messages include additional information that we don't want to display.  The additional
                    // information appears between {}. So, remove all instances of " {<any number of characters>}".
                    string errorMessage = Regex.Replace(rpcException.Status.Detail, @"\s\{[^}]+\}", string.Empty);
                    var status = new Status(rpcException.Status.StatusCode, errorMessage, rpcException.Status.DebugException);

                    throw new RpcException(status, rpcException.Trailers, errorMessage);
                }

                throw;
            }
            try
            {
                PropertyObjectInstance modelData = GetProcessModelModelData(_activeExecution, modelName);


                SetExecutionStatus(Constants.ExecutionStateRunning);

                try
                {
                    _activeExecutionCancellationTokenSource = new CancellationTokenSource();
                    await _clients.ExecutionClient.WaitForEndExAsync(new Execution_WaitForEndExRequest
                    {
                        Instance = _activeExecution,
                        MillisecondTimeOut = -1,
                        ProcessWindowsMsgs = false
                    },
                    cancellationToken: _activeExecutionCancellationTokenSource.Token);
                }
                catch (Exception exception)
                {
                    lock (_dataLock)
                    {
                        _activeExecution = null;
                        _activeExecutionCancellationTokenSource = null;
                    }

                    // When disconnecting from server, the token _activeExecutionCancellationTokenSource is cancelled.
                    // So, a cancelled exception should not be treated as an error.
                    if (exception is RpcException rpcException && rpcException.StatusCode == StatusCode.Cancelled)
                    {
                        LogLine("Waiting for execution has been cancelled.");
                    }
                    else
                    {
                        throw;
                    }
                }

                return modelData;
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception : {0}", e);
                throw e;
            }
        }

        private int GetNumberOfTestSockets(SequenceFileInstance processModel, string modelName)
        {
            int numberOfTestSockets = 1; // Default when not using a model or when using sequential model with a single test socket
            if (processModel != null && !IsSequentialModelName(modelName))
            {
                int configuredNumberOfTestSockets = GetMultipleUUTSettingsNumberOfTestSocketsOption();
                numberOfTestSockets = configuredNumberOfTestSockets > 0 ? configuredNumberOfTestSockets : Constants.DefaultNumberOfTestSockets;

                // For Parallel and Batch process models, add an additional socket for the controller execution.
                numberOfTestSockets++;
            }

            return numberOfTestSockets;
        }

        private PropertyObjectInstance GetProcessModelModelData(ExecutionInstance execution, string modelName)
        {
            if (string.IsNullOrEmpty(modelName) || string.Compare(modelName, "None", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return null;
            }

            // ModelData is a local variable in the root context.
            ThreadInstance thread = _clients.ExecutionClient.GetThread(new Execution_GetThreadRequest { Instance = execution, Index = 0 }).ReturnValue;
            SequenceContextInstance currentContext = _clients.ThreadClient.GetSequenceContext(new Thread_GetSequenceContextRequest { Instance = thread, CallStackIndex = 0 }).ReturnValue;
            SequenceContextInstance rootContext = _clients.SequenceContextClient.Get_Root(new SequenceContext_Get_RootRequest { Instance = currentContext }).ReturnValue;
            PropertyObjectInstance locals = _clients.SequenceContextClient.Get_Locals(new SequenceContext_Get_LocalsRequest { Instance = rootContext }).ReturnValue;

            PropertyObjectInstance modelData = _clients.PropertyObjectClient.GetPropertyObject(new PropertyObject_GetPropertyObjectRequest
            {
                Instance = locals,
                LookupString = "ModelData",
                Options = PropertyOptions.PropOptionNoOptions
            }).ReturnValue;

            return modelData;
        }

        private void LogExecutionResults(string resultStatus, SequenceFileInstance processModel, string modelName, PropertyObjectInstance modelData)
        {
            int numberOfResults = 0;

            if (processModel != null && !IsSequentialModelName(modelName))
            {
                numberOfResults = DisplayResultsForBatchOrParallelModelRuns(modelData, modelName);
            }
            else
            {
                bool hasResults = true;
                PropertyObjectInstance executionResults = _clients.ExecutionClient.Get_ResultObject(new Execution_Get_ResultObjectRequest { Instance = _activeExecution }).ReturnValue;

                if (processModel != null)
                {
                    if (IsSequentialModelName(modelName))
                    {
                        var resultList = _clients.PropertyObjectClient.GetPropertyObject(new PropertyObject_GetPropertyObjectRequest
                        {
                            Instance = executionResults,
                            LookupString = "ResultList",
                            Options = PropertyOptions.PropOptionNoOptions
                        }).ReturnValue;

                        var elementCount = _clients.PropertyObjectClient.GetNumElements(new PropertyObject_GetNumElementsRequest
                        {
                            Instance = resultList,
                        }).ReturnValue;

                        hasResults = elementCount == 1;
                        if (hasResults)
                        {
                            executionResults = _clients.PropertyObjectClient.GetPropertyObject(new PropertyObject_GetPropertyObjectRequest
                            {
                                Instance = executionResults,
                                LookupString = "ResultList[0].TS.SequenceCall",
                                Options = PropertyOptions.PropOptionNoOptions
                            }).ReturnValue;

                            string entryPointName = _clients.PropertyObjectClient.GetValString(new PropertyObject_GetValStringRequest
                            {
                                Instance = modelData,
                                LookupString = "EntryPoint",
                                Options = PropertyOptions.PropOptionNoOptions
                            }).ReturnValue;

                            LogLine(System.FormattableString.Invariant($"Results for '{"Test.seq"}' using '{modelName}: {entryPointName}'"));
                        }
                    }
                }
                else
                {
                    string sequenceName = _clients.PropertyObjectClient.GetValString(new PropertyObject_GetValStringRequest
                    {
                        Instance = executionResults,
                        LookupString = "Sequence",
                        Options = PropertyOptions.PropOptionNoOptions
                    }).ReturnValue;

                    LogLine(System.FormattableString.Invariant($"Results for '{"Test.seq"}: {sequenceName}'"));
                }

                if (hasResults)
                {
                    numberOfResults = DisplayResults(executionResults, indentationLevel: 0);
                }
            }

            Log("Execution Complete. Status: ");
            Log(resultStatus, GetResultBackgroundColor(resultStatus));
            LogLine(", Number of Results = " + numberOfResults.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);

            // Get the report location on the server if using a process model
            if (processModel != null)
            {
                LogFaded("Report location(s) on server:\n");
                foreach (string reportPath in _reportLocationsOnServer)
                {
                    LogLine(reportPath + Environment.NewLine);
                }
            }
        }

        private int DisplayResultsForBatchOrParallelModelRuns(PropertyObjectInstance modelData, string modelName)
        {
            int numberOfResults = 0;

            Debug.Assert(modelData != null);

            string entryPointName = _clients.PropertyObjectClient.GetValString(new PropertyObject_GetValStringRequest
            {
                Instance = modelData,
                LookupString = "EntryPoint",
                Options = PropertyOptions.PropOptionNoOptions
            }).ReturnValue;

            LogLine(System.FormattableString.Invariant($"Results for '{"Test.seq"}' using '{modelName}: {entryPointName}'"));

            // The results are under ModelData.TestSockets.
            PropertyObjectInstance testSockets = _clients.PropertyObjectClient.GetPropertyObject(new PropertyObject_GetPropertyObjectRequest
            {
                Instance = modelData,
                LookupString = "TestSockets",
                Options = PropertyOptions.PropOptionNoOptions
            }).ReturnValue;

            int numberOfTestSockets = _clients.PropertyObjectClient.GetNumElements(new PropertyObject_GetNumElementsRequest
            {
                Instance = testSockets,
            }).ReturnValue;

            for (int socketIndex = 0; socketIndex < numberOfTestSockets; socketIndex++)
            {
                string socketResultLookupString = System.FormattableString.Invariant($"[{socketIndex}].MainSequenceResults.TS.SequenceCall");
                PropertyObjectInstance socketResults = _clients.PropertyObjectClient.GetPropertyObject(new PropertyObject_GetPropertyObjectRequest
                {
                    Instance = testSockets,
                    LookupString = socketResultLookupString,
                    Options = PropertyOptions.PropOptionNoOptions
                }).ReturnValue;

                var sequenceCallStatus = _clients.PropertyObjectClient.GetValString(new PropertyObject_GetValStringRequest
                {
                    Instance = socketResults,
                    LookupString = "Status",
                    Options = PropertyOptions.PropOptionNoOptions
                }).ReturnValue;

                LogBold(System.FormattableString.Invariant($"Socket {socketIndex}: "));
                LogLine(sequenceCallStatus, GetResultBackgroundColor(sequenceCallStatus));

                numberOfResults += DisplayResults(socketResults, indentationLevel: 0);
            }

            return numberOfResults;
        }

        private static bool IsSequentialModelName(string processModelName)
        {
            return string.Compare(processModelName, Constants.SequentialModelFilename, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private void SetExecutionStatus(string executionStatus)
        {
            if (executionStatus == Constants.NotExecutedSequenceFile)
            {
                //_executionStatePictureBox.Image = null;
                //_executionStateDescriptionLabel.Text = string.Empty;
                _executionStateDescriptionLabel = string.Empty;
            }
            else
            {
                //_executionStatePictureBox.Image = new Bitmap(_imageList[executionStatus]);
                _executionStateDescriptionLabel = executionStatus;
            }
        }

        private int DisplayResults(PropertyObjectInstance resultObject, int indentationLevel)
        {
            var resultList = _clients.PropertyObjectClient.GetPropertyObject(new PropertyObject_GetPropertyObjectRequest
            {
                Instance = resultObject,
                LookupString = "ResultList",
                Options = PropertyOptions.PropOptionNoOptions
            }).ReturnValue;

            var numberOfResults = _clients.PropertyObjectClient.GetNumElements(new PropertyObject_GetNumElementsRequest
            {
                Instance = resultList
            }).ReturnValue;

            int totalNumberOfResults = numberOfResults;
            if (numberOfResults == 0)
            {
                LogLine("No results.", indentationLevel);
            }
            else
            {
                for (int index = 0; index < numberOfResults; index++)
                {
                    var nthResult = _clients.PropertyObjectClient.GetPropertyObjectByOffset(new PropertyObject_GetPropertyObjectByOffsetRequest
                    {
                        Instance = resultList,
                        ArrayOffset = index,
                        Options = PropertyOptions.PropOptionNoOptions
                    }).ReturnValue;

                    var stepName = _clients.PropertyObjectClient.GetValString(new PropertyObject_GetValStringRequest
                    {
                        Instance = nthResult,
                        LookupString = "TS.StepName",
                        Options = PropertyOptions.PropOptionNoOptions
                    }).ReturnValue;

                    var stepStatus = _clients.PropertyObjectClient.GetValString(new PropertyObject_GetValStringRequest
                    {
                        Instance = nthResult,
                        LookupString = "Status",
                        Options = PropertyOptions.PropOptionNoOptions
                    }).ReturnValue;
                    string formattedStepStatus = string.Format("{0, -7}", stepStatus);

                    Log(formattedStepStatus, GetResultBackgroundColor(stepStatus), indentationLevel: 0, overlayColor: false);
                    LogFaded("  Step ", indentationLevel);
                    LogLine(stepName);

                    if (stepStatus == Constants.ExecutionStateError)
                    {
                        double code = _clients.PropertyObjectClient.GetValNumber(new PropertyObject_GetValNumberRequest
                        {
                            Instance = nthResult,
                            LookupString = "Error.Code",
                            Options = PropertyOptions.PropOptionNoOptions
                        }).ReturnValue;

                        var message = _clients.PropertyObjectClient.GetValString(new PropertyObject_GetValStringRequest
                        {
                            Instance = nthResult,
                            LookupString = "Error.Msg",
                            Options = PropertyOptions.PropOptionNoOptions
                        }).ReturnValue;

                        // Indent the Code label below the Step label.
                        // 7 for status column + 2 for empty space before "Step" label + IndentOffsetForOneLevel
                        int labelIndentOffset = Constants.IndentOffsetForOneLevel + 9;
                        string indentedCodeLabel = string.Format("{0," + labelIndentOffset + "}Code ", "");

                        LogFaded(indentedCodeLabel, indentationLevel + 1);
                        Log(code.ToString());
                        LogFaded("  Message ");
                        LogLine(message);
                    }

                    // If the property TS.SequenceCall exists in the current result, it means it is a 
                    // sequence call. So, recurse to get those results.
                    bool isSequenceCall = _clients.PropertyObjectClient.Exists(new PropertyObject_ExistsRequest
                    {
                        Instance = nthResult,
                        LookupString = "TS.SequenceCall",
                        Options = PropertyOptions.PropOptionNoOptions
                    }).ReturnValue;
                    if (isSequenceCall)
                    {
                        var sequenceCall = _clients.PropertyObjectClient.GetPropertyObject(new PropertyObject_GetPropertyObjectRequest
                        {
                            Instance = nthResult,
                            LookupString = "TS.SequenceCall",
                            Options = PropertyOptions.PropOptionNoOptions
                        }).ReturnValue;

                        totalNumberOfResults += DisplayResults(sequenceCall, indentationLevel + 1);
                    }
                }
            }

            return totalNumberOfResults;
        }

        private void OnLoad(object sender, EventArgs e)
        {
            // Add some space between the lines in the log and trace messages text.
            // The space will make the lines more readable.
            //SetLineSpacing(_logTextBox);
            //SetLineSpacing(_executionTraceMessagesTextBox);

            //_processModelComboBox.SelectedIndex = 0;
            //_entryPointComboBox.SelectedIndex = 0;

            //if (_clientOptions.ExampleFiles != null && _clientOptions.ExampleFiles.Length > 0)
            //{
            //    _sequenceFileNameComboBox.Items.AddRange(_clientOptions.ExampleFiles);
            //}
            //else
            //{
            //    string tooltip = "Add a list of files to the property 'example_files' in the config file 'client_config.json'.\n" +
            //        "Restart the client application to show the list.";
            //    _sequenceFileNameComboBoxToolTip = new ToolTipEx(this, _sequenceFileNameComboBox, tooltip);

            //    _sequenceFileNameComboBox.Items.Add("No Files Configured");
            //    _sequenceFileNameComboBox.Enabled = false;
            //    _runSequenceFileButton.Enabled = false;
            //}
            //_sequenceFileNameComboBox.SelectedIndex = 0;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Cleanup();
        }

        private void OnClearOutputButtonClick(object sender, EventArgs e)
        {
            //_logTextBox.Text = string.Empty;
        }

        public async void OnBreakButtonClick(object sender, EventArgs e)
        {
            await TryActionAsync(async () =>
            {
                using (new AutoWaitCursor(this))
                {
                    await ApplyActionOnOpenExecutionsAsync(ExecutionAction.Break);
                    await UpdateExecutionOptionsStateAsync();
                }
            }, "Breaking execution");
        }

        public async void OnResumeButtonClick(object sender, EventArgs e)
        {
            await TryActionAsync(async () =>
            {
                using (new AutoWaitCursor(this))
                {
                    await ApplyActionOnOpenExecutionsAsync(ExecutionAction.Resume);
                    await UpdateExecutionOptionsStateAsync();
                }
            }, "Resuming execution");
        }

        public async void OnTerminateButtonClick(object sender, EventArgs e)
        {
            await TryActionAsync(async () =>
            {
                using (new AutoWaitCursor(this))
                {
                    await ApplyActionOnOpenExecutionsAsync(ExecutionAction.Terminate);
                    SetExecutionStatus(Constants.ExecutionStateTerminating);
                    await UpdateExecutionOptionsStateAsync();
                }
            }, "Terminating execution.");
        }

        private async Task ApplyActionOnOpenExecutionsAsync(ExecutionAction action)
        {
            List<ExecutionInfo> executionInfos = GetOpenExecutionsAssociatedWithActiveExecution(getThreadInfo: false);

            switch (action)
            {
                case ExecutionAction.Break:
                    {
                        var tasksToWaitOn = new List<AsyncUnaryCall<Execution_BreakResponse>>(executionInfos.Count);
                        foreach (ExecutionInfo executionInfo in executionInfos)
                        {
                            if (executionInfo.RunState == ExecutionRunStates.ExecRunStateRunning)
                            {
                                tasksToWaitOn.Add(_clients.ExecutionClient.BreakAsync(new Execution_BreakRequest { Instance = executionInfo.Instance }));
                            }
                        }
                        await Task.WhenAll(tasksToWaitOn.Select(asynCall => asynCall.ResponseAsync));
                    }
                    break;
                case ExecutionAction.Resume:
                    {
                        var tasksToWaitOn = new List<AsyncUnaryCall<Execution_ResumeResponse>>(executionInfos.Count);
                        foreach (ExecutionInfo executionInfo in executionInfos)
                        {
                            if (executionInfo.RunState == ExecutionRunStates.ExecRunStatePaused)
                            {
                                tasksToWaitOn.Add(_clients.ExecutionClient.ResumeAsync(new Execution_ResumeRequest { Instance = executionInfo.Instance }));
                            }
                        }
                        await Task.WhenAll(tasksToWaitOn.Select(asynCall => asynCall.ResponseAsync));
                    }
                    break;
                case ExecutionAction.Terminate:
                    {
                        var tasksToWaitOn = new List<AsyncUnaryCall<Execution_TerminateResponse>>(executionInfos.Count);
                        foreach (ExecutionInfo executionInfo in executionInfos)
                        {
                            if (executionInfo.RunState != ExecutionRunStates.ExecRunStateStopped)
                            {
                                tasksToWaitOn.Add(_clients.ExecutionClient.TerminateAsync(new Execution_TerminateRequest { Instance = executionInfo.Instance }));
                            }
                        }
                        await Task.WhenAll(tasksToWaitOn.Select(asynCall => asynCall.ResponseAsync));
                    }
                    break;
            }
        }

        private async Task UpdateExecutionOptionsStateAsync()
        {
            bool breakEnabled = false;
            bool resumeEnabled = false;
            bool terminateEnabled = false;
            string suspendedAtStepName = string.Empty;
            ExecutionInstance activeExecution;

            // Since an await cannot be inside a lock, we need to copy the executions to make sure
            // the list is not updated while refreshing the button states.
            lock (_dataLock)
            {
                activeExecution = _activeExecution;
            }

            if (activeExecution != null)
            {
                var states = await _clients.ExecutionClient.GetStatesAsync(new Execution_GetStatesRequest { Instance = activeExecution });

                if (_executionStateDescriptionLabel != Constants.ExecutionStateTerminating)
                {
                    if (states.RunState == ExecutionRunStates.ExecRunStatePaused)
                    {
                        SetExecutionStatus(Constants.ExecutionStatePaused);
                    }
                    else if (states.RunState == ExecutionRunStates.ExecRunStateRunning)
                    {
                        SetExecutionStatus(Constants.ExecutionStateRunning);
                    }
                }

                breakEnabled = states.RunState == ExecutionRunStates.ExecRunStateRunning;
                resumeEnabled = states.RunState == ExecutionRunStates.ExecRunStatePaused;
                terminateEnabled = states.RunState != ExecutionRunStates.ExecRunStateStopped;

                if (resumeEnabled)
                {
                    // When using Batch or Parallel models, we need to show the model name for the suspended step
                    // since the executions can be stopped at different steps.
                    if (_nonSequentialProcessModelName != null)
                    {
                        suspendedAtStepName = "<" + _nonSequentialProcessModelName + ">";
                    }
                    else
                    {
                        var mainExecutionThread = _clients.ExecutionClient.Get_ForegroundThread(new Execution_Get_ForegroundThreadRequest { Instance = activeExecution }).ReturnValue;
                        var sequenceContext = _clients.ThreadClient.GetSequenceContext(new Thread_GetSequenceContextRequest { Instance = mainExecutionThread, CallStackIndex = 0 }).ReturnValue;
                        var nextStepIndex = _clients.SequenceContextClient.Get_NextStepIndex(new SequenceContext_Get_NextStepIndexRequest { Instance = sequenceContext }).ReturnValue;
                        if (nextStepIndex >= 0)
                        {
                            var step = _clients.SequenceContextClient.Get_NextStep(new SequenceContext_Get_NextStepRequest { Instance = sequenceContext }).ReturnValue;
                            suspendedAtStepName = _clients.StepClient.Get_Name(new Step_Get_NameRequest { Instance = step }).ReturnValue;
                        }
                    }
                }
            }

            //_suspendedAtStepTextBox.Text = suspendedAtStepName;
            //_suspendedAtStepTextBox.Enabled = !string.IsNullOrEmpty(suspendedAtStepName);

            //_breakButton.Enabled = breakEnabled;
            //_resumeButton.Enabled = resumeEnabled;
            //_terminateButton.Enabled = terminateEnabled;
        }

        private void OnUpdateExecutionOptionsStateTimerTick(object sender, EventArgs e)
        {
            _ = TryActionAsync(async () => await UpdateExecutionOptionsStateAsync(), null);
        }

        private void OnServerHeartbeatTimerTick(object sender, EventArgs e)
        {
            // To check the connection to the server, we need to do a simple-non-updating Engine call. If the call
            // fails with the status code set to Unavailable, we know the connection to the server has been lost.
            if (_isConnected)
            {
                try
                {
                    _clients.EngineClient.Get_MajorVersion(new Engine_Get_MajorVersionRequest { Instance = _engine });
                }
                catch (Exception exception)
                {
                    if (exception is RpcException rpcException && rpcException.StatusCode == StatusCode.Unavailable)
                    {
                        LogLine(Environment.NewLine + "ERROR: Lost connection to the server.");
                        SetConnectionStatus(false);
                    }
                    else
                    {
                        ReportException(exception);
                    }
                }
            }
        }

        // This method gets called when changing the selection in the list view
        private void OnStationGlobalsListViewSelectedIndexChanged(object sender, EventArgs e)
        {
            //bool canEditValue = false;

            //if (_stationGlobalsListView.SelectedIndices.Count > 0)
            //{
            //    _deleteGlobalButton.Enabled = true;
            //    _booleanValueComboBox.Visible = false;
            //    _valueTextBox.Visible = true;

            //    _valueForSelectedItemIndex = _stationGlobalsListView.SelectedIndices[0];
            //    ListViewItem selectedItem = _stationGlobalsListView.Items[_valueForSelectedItemIndex];
            //    string typeString = selectedItem.SubItems[2].Text;

            //    // Populate the value text box if a string or number is selected.
            //    if (typeString == "Number" || typeString == "String")
            //    {
            //        _valueTextBox.Text = selectedItem.SubItems[1].Text;
            //        _valueTextBox.Enabled = true;
            //        canEditValue = true;
            //    }
            //    // Else, show the combobox and set it to the boolean property value
            //    else if (typeString == "Boolean")
            //    {
            //        _booleanValueComboBox.Visible = true;
            //        _valueTextBox.Visible = false;
            //        _booleanValueComboBox.SelectedIndex = selectedItem.SubItems[1].Text == "True" ? 1 : 0;
            //        canEditValue = true;
            //    }
            //}

            //if (!canEditValue)
            //{
            //    _deleteGlobalButton.Enabled = false;
            //    _valueTextBox.Text = string.Empty;
            //    _valueTextBox.Enabled = false;
            //    _booleanValueComboBox.Visible = false;
            //    _valueForSelectedItemIndex = -1;
            //}
        }

        // Will commit the value of the text box when focus changes
        private void OnValueTextBoxLeave(object sender, EventArgs e)
        {
            //if (_valueTextBox.Enabled)
            //{
            //    SetValueOnGlobalVariable(_valueTextBox.Text);
            //}
        }

        //private void OnValueTextBoxKeyDown(object sender, KeyEventArgs e)
        //{
        //    //if (e.KeyCode == Keys.Enter)
        //    //{
        //    //    SetValueOnGlobalVariable(_valueTextBox.Text);
        //    //}
        //}

        private void OnAddStationGlobalClick(object sender, EventArgs e)
        {
            PropertyObjectInstance stationGlobals = _clients.EngineClient.Get_Globals(new Engine_Get_GlobalsRequest { Instance = _engine }).ReturnValue;
            int numberOfGlobals = _clients.PropertyObjectClient.GetNumSubProperties(
                new PropertyObject_GetNumSubPropertiesRequest
                {
                    Instance = stationGlobals,
                    LookupString = string.Empty
                }).ReturnValue;

            string newGlobalName = "NumericGlobal_" + numberOfGlobals;

            _clients.PropertyObjectClient.NewSubProperty(
                new PropertyObject_NewSubPropertyRequest
                {
                    Instance = stationGlobals,
                    LookupString = newGlobalName,
                    ValueType = PropertyValueTypes.PropValTypeNumber,
                    AsArray = false,
                    TypeNameParam = string.Empty,
                    Options = PropertyOptions.PropOptionNoOptions
                });

            RefreshStationGlobals();
            // _commitGlobalsToDiskButton.Enabled = true;
        }

        private void OnDeleteStationGlobalClick(object sender, EventArgs e)
        {
            //ListViewItem selectedItem = _stationGlobalsListView.Items[_stationGlobalsListView.SelectedIndices[0]];
            //PropertyObjectInstance stationGlobals = _clients.EngineClient.Get_Globals(new Engine_Get_GlobalsRequest { Instance = _engine }).ReturnValue;
            //_clients.PropertyObjectClient.DeleteSubProperty(new PropertyObject_DeleteSubPropertyRequest
            //{
            //    Instance = stationGlobals,
            //    LookupString = selectedItem.Text,
            //    Options = PropertyOptions.PropOptionNoOptions
            //});

            //RefreshStationGlobals();
            //_commitGlobalsToDiskButton.Enabled = true;
        }

        private void OnBooleanValueComboBoxSelectionChangedCommitted(object sender, EventArgs e)
        {
            //SetValueOnGlobalVariable((string)_booleanValueComboBox.SelectedItem);
        }

        private void OnCommitGlobalsToDiskClick(object sender, EventArgs e)
        {
            _clients.EngineClient.CommitGlobalsToDisk(new Engine_CommitGlobalsToDiskRequest { Instance = _engine, PromptOnSaveConflicts = true });
            //_commitGlobalsToDiskButton.Enabled = false;
        }

        //private void OnStationModelComboBoxSelectedIndexChanged(object sender, EventArgs e)
        //{
        //    StationOptionsInstance stationOptions = GetStationOptions();
        //    _clients.StationOptionsClient.Set_StationModelSequenceFilePath(
        //        new StationOptions_Set_StationModelSequenceFilePathRequest
        //        {
        //            Instance = stationOptions,
        //            ModelPath = _stationModelComboBox.Text
        //        });

        //    EnableOrDisableProcessModelOptionAndNumberOfTestSockets();
        //}

        //private void OnNumTestSocketNumericUpDownValueChanged(object sender, EventArgs e)
        //{
        //    SetMultipleUUTSettingsNumberOfTestSocketsOption((int)_numTestSocketsNumericUpDown.Value);
        //}

        //private void SetValueOnGlobalVariable(string newValue)
        //{
        //    ListViewItem selectedItem = _stationGlobalsListView.Items[_valueForSelectedItemIndex];
        //    PropertyObjectInstance stationGlobals = _clients.EngineClient.Get_Globals(new Engine_Get_GlobalsRequest { Instance = _engine }).ReturnValue;
        //    PropertyObjectInstance globalObject = _clients.PropertyObjectClient.GetPropertyObject(
        //        new PropertyObject_GetPropertyObjectRequest
        //        {
        //            Instance = stationGlobals,
        //            LookupString = selectedItem.Text,
        //            Options = PropertyOptions.PropOptionNoOptions
        //        }).ReturnValue;

        //    _clients.PropertyObjectClient.SetValString(
        //        new PropertyObject_SetValStringRequest
        //        {
        //            Instance = globalObject,
        //            LookupString = string.Empty,
        //            NewValue = newValue,
        //            Options = PropertyOptions.PropOptionCoerce
        //        });

        //    RefreshStationGlobals();
        //    _commitGlobalsToDiskButton.Enabled = true;
        //}

        private void OnClearExecutionTraceMessagesButtonClick(object sender, EventArgs e)
        {
            ClearTraceMessages();
        }

        private void IdentifyThread(ThreadInstance thread, out ThreadInfo threadInfo)
        {
            SequenceContextInstance rootContext;

            threadInfo = new ThreadInfo();

            do
            {
                var currentContext = _clients.ThreadClient.GetSequenceContext(new Thread_GetSequenceContextRequest { Instance = thread, CallStackIndex = 0 }).ReturnValue;
                rootContext = _clients.SequenceContextClient.Get_Root(new SequenceContext_Get_RootRequest { Instance = currentContext }).ReturnValue;
            } while (rootContext.Id == "0");  // if we ask at the wrong time (frame just ended), we might get zero (null). Not sure of a way to get the root 100% without retries if execution is not paused.

            var locals = _clients.SequenceContextClient.Get_Locals(new SequenceContext_Get_LocalsRequest { Instance = rootContext }).ReturnValue;
            var modelThreadTypeLocalExists = _clients.PropertyObjectClient.Exists(new PropertyObject_ExistsRequest { Instance = locals, LookupString = "ModelThreadType", Options = PropertyOptions.PropOptionNoOptions }).ReturnValue;

            if (modelThreadTypeLocalExists)
            {
                threadInfo.IsController = _clients.PropertyObjectClient.GetValBoolean(new PropertyObject_GetValBooleanRequest { Instance = locals, LookupString = "ModelThreadType.IsController", Options = PropertyOptions.PropOptionNoOptions }).ReturnValue;
                threadInfo.IsTestSocket = _clients.PropertyObjectClient.GetValBoolean(new PropertyObject_GetValBooleanRequest { Instance = locals, LookupString = "ModelThreadType.IsTestSocket", Options = PropertyOptions.PropOptionNoOptions }).ReturnValue;

                var propertyObjectInstance = new PropertyObjectInstance() { Id = rootContext.Id };
                threadInfo.SocketIndex = (int)_clients.PropertyObjectClient.GetValNumber(new PropertyObject_GetValNumberRequest { Instance = propertyObjectInstance, LookupString = "Runstate.TestSockets.MyIndex", Options = PropertyOptions.PropOptionNoOptions }).ReturnValue;
            }

            if (threadInfo.IsTestSocket && !threadInfo.IsController)
            {
                var parameters = _clients.SequenceContextClient.Get_Parameters(new SequenceContext_Get_ParametersRequest { Instance = rootContext }).ReturnValue;
                var parentControllerThread = _clients.PropertyObjectClient.GetValInterface(new PropertyObject_GetValInterfaceRequest { Instance = parameters, LookupString = "ParentThread", Options = PropertyOptions.PropOptionNoOptions }).ReturnValue;
                var threadInstance = new ThreadInstance { Id = parentControllerThread.Id };
                var execution = _clients.ThreadClient.Get_Execution(new Thread_Get_ExecutionRequest { Instance = threadInstance }).ReturnValue;
                threadInfo.ParentControllerThreadId = _clients.ThreadClient.Get_Id(new Thread_Get_IdRequest { Instance = threadInstance }).ReturnValue;
                threadInfo.ParentControllerExecutionId = _clients.ExecutionClient.Get_Id(new Execution_Get_IdRequest { Instance = execution }).ReturnValue;
            }
        }

        private void ListExecutionsAndThreads()
        {
            if (_clients.EngineClient == null)
            {
                LogLine("NOT CONNECTED");
            }

            if (_activeExecution == null)
            {
                LogLine("NO EXECUTIONS");
            }

            List<ExecutionInfo> executionInfos = GetOpenExecutionsAssociatedWithActiveExecution(getThreadInfo: true);

            for (int executionIndex = 0; executionIndex < executionInfos.Count; executionIndex++)
            {
                ExecutionInfo executionInfo = executionInfos[executionIndex];

                LogLine(System.FormattableString.Invariant($"Execution #{executionIndex} - {executionInfo.Name}"));

                for (int threadIndex = 0; threadIndex < executionInfo.Threads.Count; threadIndex++)
                {
                    ThreadInfo threadInfo = executionInfo.Threads[threadIndex];

                    Log(System.FormattableString.Invariant($"    Thread #{threadIndex} - {threadInfo.Name} [Controller = {threadInfo.IsController}, Socket = {threadInfo.IsTestSocket}, Socket Index = {threadInfo.SocketIndex}"));
                    if (threadInfo.ParentControllerThreadId != 0)
                    {
                        Log(System.FormattableString.Invariant($", Parent (Controller) Thread Id = {threadInfo.ParentControllerThreadId}"));
                    }
                    LogLine(string.Empty);
                }
            }
        }

        private List<ExecutionInfo> GetOpenExecutionsAssociatedWithActiveExecution(bool getThreadInfo)
        {
            var executionInstances = new List<ExecutionInfo>();

            if (_activeExecution == null)
            {
                return executionInstances;
            }

            ExecutionsInstance executions = GetAllOpenExecutions();

            var numberOfExecutions = _clients.ExecutionsClient.Get_Count(new Executions_Get_CountRequest { Instance = executions }).ReturnValue;
            int activeExecutionId = _clients.ExecutionClient.Get_Id(new Execution_Get_IdRequest { Instance = _activeExecution }).ReturnValue;

            // For each execution, check if the id or the parent execution id matches the active execution.
            for (int executionIndex = 0; executionIndex < numberOfExecutions; executionIndex++)
            {
                var execution = _clients.ExecutionsClient.Get_Item(new Executions_Get_ItemRequest { Instance = executions, ItemIdx = executionIndex }).ReturnValue;
                var numberOfThreads = _clients.ExecutionClient.Get_NumThreads(new Execution_Get_NumThreadsRequest { Instance = execution }).ReturnValue;

                // If the number of threads is zero, it means the execution has completed so skip it.
                if (numberOfThreads == 0)
                {
                    continue;
                }

                var executionInfo = new ExecutionInfo(execution)
                {
                    ExecutionId = _clients.ExecutionClient.Get_Id(new Execution_Get_IdRequest { Instance = execution }).ReturnValue
                };

                bool executionStartedByClient = executionInfo.ExecutionId == activeExecutionId;
                if (!executionStartedByClient)
                {
                    // Get the thread info which includes the execution parent id
                    var thread = _clients.ExecutionClient.GetThread(new Execution_GetThreadRequest { Instance = execution, Index = 0 }).ReturnValue;
                    IdentifyThread(thread, out ThreadInfo threadInfo);

                    executionStartedByClient = threadInfo.ParentControllerExecutionId == activeExecutionId;
                    if (executionStartedByClient && getThreadInfo)
                    {
                        threadInfo.Name = _clients.ThreadClient.Get_DisplayName(new Thread_Get_DisplayNameRequest { Instance = thread }).ReturnValue;
                        executionInfo.Threads.Add(threadInfo);
                    }
                }

                if (executionStartedByClient)
                {
                    executionInfo.Name = _clients.ExecutionClient.Get_DisplayName(new Execution_Get_DisplayNameRequest { Instance = execution }).ReturnValue;
                    executionInfo.RunState = _clients.ExecutionClient.GetStates(new Execution_GetStatesRequest { Instance = execution }).RunState;

                    if (getThreadInfo)
                    {
                        for (int threadIndex = 1; threadIndex < numberOfThreads; threadIndex++)
                        {
                            var thread = _clients.ExecutionClient.GetThread(new Execution_GetThreadRequest { Instance = execution, Index = threadIndex }).ReturnValue;
                            IdentifyThread(thread, out ThreadInfo threadInfo);
                            threadInfo.Name = _clients.ThreadClient.Get_DisplayName(new Thread_Get_DisplayNameRequest { Instance = thread }).ReturnValue;
                            executionInfo.Threads.Add(threadInfo);
                        }
                    }

                    executionInstances.Add(executionInfo);
                }
            }

            return executionInstances;
        }

        private ExecutionsInstance GetAllOpenExecutions()
        {
            var applicationMgr = new ApplicationMgrInstance { Id = "ApplicationMgr" };

            return _clients.ApplicationMgrClient.Get_Executions(new ApplicationMgr_Get_ExecutionsRequest { Instance = applicationMgr }).ReturnValue;
        }

        private void OnListThreadsButtonClick(object sender, EventArgs e)
        {
            try
            {
                ListExecutionsAndThreads();
            }
            catch (Exception exception)
            {
                ReportException(exception);
            }
        }

        private void OnEnableTracingCheckBoxCheckStateChanged(object sender, EventArgs e)
        {
            StationOptionsInstance stationOptions = GetStationOptions();
            _clients.StationOptionsClient.Set_TracingEnabled(
                new StationOptions_Set_TracingEnabledRequest
                {
                    Instance = stationOptions,
                    //IsEnabled = _enableTracingCheckBox.Checked
                    IsEnabled = true
                });

            UpdateTraceMessagesControls();

            Log("Tracing on the server is " + (true ? "enabled." : "disabled.") + Environment.NewLine);
        }

        private static Color GetResultBackgroundColor(string stepStatus)
        {
            return stepStatus.Trim() switch
            {
                Constants.ExecutionStateError or Constants.ExecutionStateFailed => Color.FromArgb(241, 178, 185),
                Constants.ExecutionStatePassed => Color.FromArgb(187, 237, 196),
                Constants.StepResultSkipped => Color.FromArgb(192, 192, 192),
                _ => SystemColors.Window,
            };
        }

        private void LogLine(string lineToLog, int indentationLevel = 0)
        {
            Log(lineToLog + Environment.NewLine, indentationLevel);
        }

        private void Log(string stringToLog, int indentationLevel = 0)
        {
            //stringToLog = GetIndentSpace(indentationLevel) + stringToLog;
            //this.BeginInvoke(() =>  // BeginInvoke, so this can be called from any thread without blocking
            //{
            //    _logTextBox.AppendText(stringToLog);
            //    ScrollToBottomOfText(_logTextBox);
            //});

            Console.WriteLine("Log: {0}", stringToLog);
        }

        /// <summary>
        /// Appends the given line to the log control
        /// </summary>
        /// <param name="lineToLog">The line of text to append</param>
        /// <param name="textBackgroundColor">The background color to use to highlight the text</param>
        /// <param name="indentationLevel">The indentation level of the text</param>
        /// <param name="overlayColor">When true, the text is highlighted. When false, a color box will be added to the left of the text.</param>
        private void LogLine(string lineToLog, Color textBackgroundColor, int indentationLevel = 0, bool overlayColor = true)
        {
            Log(lineToLog + Environment.NewLine, textBackgroundColor, indentationLevel, overlayColor);
        }

        /// <summary>
        /// Appends the given string to the log control
        /// </summary>
        /// <param name="lineToLog">The line of text to append</param>
        /// <param name="textBackgroundColor">The background color to use to highlight the text</param>
        /// <param name="indentationLevel">The indentation level of the text</param>
        /// <param name="overlayColor">When true, the text is highlighted. When false, a color box will be added to the left of the text.</param>
        private void Log(string stringToLog, Color textBackgroundColor, int indentationLevel = 0, bool overlayColor = true)
        {
            Console.WriteLine("Log with 4 params: {0}", stringToLog);
            // BeginInvoke, so this can be called from any thread without blocking
            // this.BeginInvoke(() => LogImpl(stringToLog, textBackgroundColor, indentationLevel, overlayColor));
        }

        private void LogImpl(string stringToLog, Color textBackgroundColor, int indentationLevel = 0, bool overlayColor = true)
        {
            //int indentOffset = indentationLevel * Constants.IndentOffsetForOneLevel;
            //int startSelection = _logTextBox.TextLength + indentOffset;
            //Color originalSelectionBackgroundColor = _logTextBox.SelectionBackColor;

            //stringToLog = GetIndentSpace(indentationLevel) + stringToLog;

            //int selectionLength;
            //if (overlayColor)
            //{
            //    selectionLength = stringToLog.Length;
            //}
            //else
            //{
            //    // When not overlaying the color, we need to add a box with the given color before the text.
            //    selectionLength = Constants.ColorBoxWidth;
            //    stringToLog = stringToLog.Insert(indentOffset, ColorBoxSpace);
            //}

            //_logTextBox.AppendText(stringToLog);

            //_logTextBox.Select(startSelection, selectionLength);
            //_logTextBox.SelectionBackColor = textBackgroundColor;

            //// Reset selection and color
            //_logTextBox.Select(startSelection + selectionLength, 0);
            //_logTextBox.SelectionBackColor = originalSelectionBackgroundColor;

            //ScrollToBottomOfText(_logTextBox);
        }

        private void LogBold(string stringToLog, int indentationLevel = 0)
        {
            LogBoldImpl(stringToLog, indentationLevel);
            // BeginInvoke, so this can be called from any thread without blocking
            // BeginInvoke(() => LogBoldImpl(stringToLog, indentationLevel));
        }

        private void LogBoldImpl(string stringToLog, int indentationLevel = 0)
        {
            Console.WriteLine("Log Bold Impl: {0}", stringToLog);

            //int startSelection = _logTextBox.TextLength;
            //Font originalSelectionFont = _logTextBox.SelectionFont;

            //stringToLog = GetIndentSpace(indentationLevel) + stringToLog;

            //_logTextBox.AppendText(stringToLog);
            //_logTextBox.Select(startSelection, stringToLog.Length);
            //_logTextBox.SelectionFont = new Font(_logTextBox.Font, FontStyle.Bold);

            //// Reset selection and font
            //_logTextBox.Select(startSelection + stringToLog.Length, 0);
            //_logTextBox.SelectionFont = originalSelectionFont;

            //ScrollToBottomOfText(_logTextBox);
        }

        private void LogFaded(string stringToLog, int indentationLevel = 0)
        {
            LogFadedImpl(stringToLog, indentationLevel);
            // BeginInvoke, so this can be called from any thread without blocking
            //BeginInvoke(() => LogFadedImpl(stringToLog, indentationLevel));
        }

        private void LogFadedImpl(string stringToLog, int indentationLevel = 0)
        {
            Console.WriteLine("Log Faded Impl: {0}", stringToLog);
            //int startSelection = _logTextBox.TextLength;
            //Color originalSelectionColor = _logTextBox.SelectionColor;

            //stringToLog = GetIndentSpace(indentationLevel) + stringToLog;

            //_logTextBox.AppendText(stringToLog);
            //_logTextBox.Select(startSelection, stringToLog.Length);
            //_logTextBox.SelectionColor = Color.FromArgb(129, 131, 134); // Light gray

            //// Reset selection and color
            //_logTextBox.Select(startSelection + stringToLog.Length, 0);
            //_logTextBox.SelectionColor = originalSelectionColor;
        }

        //private static void ScrollToBottomOfText(RichTextBox richTextBox)
        //{
        //    richTextBox.SelectionStart = richTextBox.Text.Length;
        //    richTextBox.ScrollToCaret();
        //}

        private void LogTraceMessage(string traceMessage)
        {
            // BeginInvoke, so this can be called from any thread without blocking
            //Debug.Assert(_enableTracingCheckBox.Checked);
            //BeginInvoke(() => LogTraceMessageImpl(traceMessage));

            Console.WriteLine("LogTrace : {0}", traceMessage);
        }


        private void LogTraceMessageImpl(string traceMessage)
        {
            Console.WriteLine("Log Trace Message Impl: {0}", traceMessage);
            //int startSearchOffset = _executionTraceMessagesTextBox.Text.Length;

            //traceMessage = InsertColorBoxSpaceToTraceMessage(traceMessage);

            //_executionTraceMessagesTextBox.AppendText(traceMessage);

            //FadeLabel("Socket", startSearchOffset);
            //AddColorBoxToTraceResult(startSearchOffset);
            //FadeLabel("Step", startSearchOffset);

            //// If trace message has an error, fade the error labels
            //if (traceMessage.IndexOf("Code") != -1)
            //{
            //    // Start searching on the error message line
            //    int indexOfNewLine = traceMessage.IndexOf('\n');
            //    startSearchOffset += indexOfNewLine;

            //    FadeLabel("Code", startSearchOffset);
            //    FadeLabel("Message", startSearchOffset);
            //}

            //ScrollToBottomOfText(_executionTraceMessagesTextBox);
        }

        private string InsertColorBoxSpaceToTraceMessage(string traceMessage)
        {
            //int startIndex = traceMessage.IndexOf("Step");
            //if (startIndex != -1)
            //{
            //    // Insertion starts at the beginning of status.
            //    // Add 2 additional spaces to include space between status and "Step" label;
            //    startIndex -= (Constants.StatusLength + 2);

            //    // Insert status box color space
            //    traceMessage = traceMessage.Insert(startIndex, ColorBoxSpace);

            //    // If trace message has an error, we need to indent the error line as well.
            //    if (traceMessage.IndexOf("Code") != -1)
            //    {
            //        int indexOfNewLine = traceMessage.IndexOf('\n');
            //        if (indexOfNewLine != -1)
            //        {
            //            traceMessage = traceMessage.Insert(indexOfNewLine + 1, ColorBoxSpace);
            //        }
            //    }
            //}

            return traceMessage;
        }

        private void FadeLabel(string label, int startSearchOffset)
        {
            //int startSelection = _executionTraceMessagesTextBox.Text.IndexOf(label, startSearchOffset);
            //if (startSelection != -1)
            //{
            //    Color originalSelectionColor = _executionTraceMessagesTextBox.SelectionColor;

            //    _executionTraceMessagesTextBox.Select(startSelection, label.Length);
            //    _executionTraceMessagesTextBox.SelectionColor = Color.FromArgb(129, 131, 134);

            //    // Reset selection and color
            //    _executionTraceMessagesTextBox.Select(startSelection + label.Length, 0);
            //    _executionTraceMessagesTextBox.SelectionColor = originalSelectionColor;
            //}
        }

        //private void AddColorBoxToTraceResult(int startSearchOffset)
        //{
        //    int startSelection = _executionTraceMessagesTextBox.Text.IndexOf("Step", startSearchOffset);
        //    if (startSelection != -1)
        //    {
        //        // Selection starts at the beginning of the colorbox space.
        //        // Add 2 additional spaces to include space between status and "Step" label;
        //        startSelection -= (Constants.StatusLength + ColorBoxSpace.Length + 2);

        //        Color originalSelectionBackgroundColor = _executionTraceMessagesTextBox.SelectionBackColor;
        //        string status = _executionTraceMessagesTextBox.Text.Substring(startSelection + ColorBoxSpace.Length, Constants.StatusLength);

        //        _executionTraceMessagesTextBox.Select(startSelection, Constants.ColorBoxWidth);
        //        _executionTraceMessagesTextBox.SelectionBackColor = GetResultBackgroundColor(status);

        //        // Reset selection and color
        //        _executionTraceMessagesTextBox.Select(startSelection + Constants.ColorBoxWidth, 0);
        //        _executionTraceMessagesTextBox.SelectionBackColor = originalSelectionBackgroundColor;
        //    }
        //}

        private string GetIndentSpace(int indentationLevel)
        {
            string indentSpace = string.Empty;
            while (indentationLevel > 0)
            {
                // indentSpace = IndentSpace + indentSpace;
                indentationLevel--;
            }

            return indentSpace;
        }

        private void ClearTraceMessages()
        {
            //_executionTraceMessagesTextBox.Text = string.Empty;
        }

        private void SetBusy()
        {
            lock (_dataLock)
            {
                _busyCount++;
                if (_busyCount == 1)
                {
                    //_previousCursor = Cursor.Current;
                    //Cursor.Current = Cursors.WaitCursor;
                }
            }
        }

        private void UnsetBusy()
        {
            lock (_dataLock)
            {
                Debug.Assert(_busyCount > 0, "Busy count is not greater than zero.");
                _busyCount--;

                if (_busyCount == 0)
                {
                    // Cursor.Current = _previousCursor;
                }
            }
        }

        //private static void SetLineSpacing(RichTextBox richTextBox)
        //{
        //    // The only way to set line spacing on a RichTextBox is
        //    // through a EM_SETPARAFORMAT message.

        //    var paraformat = new PARAFORMAT2();
        //    paraformat.cbSize = (uint)Marshal.SizeOf(paraformat);
        //    paraformat.wReserved = 0;
        //    paraformat.dwMask = (uint)RichTextBoxOptions.PFM_LINESPACING;
        //    paraformat.dyLineSpacing = 25;  // 1.25 line spacing

        //    // The value of dyLineSpacing/20 is the spacing, in lines, from one line to the next.
        //    // Thus, setting dyLineSpacing to 20 produces single-spaced text, 40 is double spaced,
        //    // 60 is triple spaced, and so on.
        //    paraformat.bLineSpacingRule = 5;

        //    IntPtr lParam = IntPtr.Zero;
        //    try
        //    {
        //        lParam = Marshal.AllocHGlobal(Marshal.SizeOf(paraformat));
        //        Marshal.StructureToPtr(paraformat, lParam, false);

        //        SendMessage(
        //            new HandleRef(richTextBox, richTextBox.Handle),
        //            (int)WindowsMessage.EM_SETPARAFORMAT,
        //            new IntPtr((int)RichTextBoxOptions.SCF_SELECTION),
        //            lParam);
        //    }
        //    finally
        //    {
        //        if (lParam != IntPtr.Zero)
        //        {
        //            Marshal.FreeHGlobal(lParam);
        //        }
        //    }
        //}

        private class AutoWaitCursor : IDisposable
        {
            private readonly Example _exampleApplication;
            private bool _disposedValue = false;

            public AutoWaitCursor(Example exampleApplication)
            {
                _exampleApplication = exampleApplication;
                exampleApplication.SetBusy();
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!_disposedValue)
                {
                    if (disposing)
                    {
                        _exampleApplication.UnsetBusy();
                    }

                    _disposedValue = true;
                }
            }

            public void Dispose()
            {
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }

        //private void Example_FormClosed(object sender, FormClosedEventArgs e)
        //{
        //    Cleanup();
        //}

    }
}