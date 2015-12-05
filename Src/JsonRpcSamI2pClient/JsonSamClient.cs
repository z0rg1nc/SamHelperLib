using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.JsonRpcHelpers.Client;
using BtmI2p.MiscUtils;
using BtmI2p.Newtonsoft.Json;
using BtmI2p.Newtonsoft.Json.Linq;
using BtmI2p.SamHelper;
using LinFu.DynamicProxy;
using NLog;

namespace BtmI2p.JsonRpcSamI2pClient
{
    public enum EJsonRpcSamI2PMessageKinds : byte
    {
        SendCall,
        ReceiveError,
        ReceiveCallbackPing,
        ReceiveResult
    }
    
    [AttributeUsage(AttributeTargets.Method)]
    public class JsonSamOperationContractAttribute : Attribute
    {
        public bool CallReliable { get; private set; }
        public bool ReturnResultReliable { get; private set; }
        public JsonSamOperationContractAttribute(
            bool callReliable = false, 
            bool resultReliable = false
        )
        {
            CallReliable = callReliable;
            ReturnResultReliable = resultReliable;
        }
    }
    public class I2PTimeoutException : TimeoutException
    {
        public I2PTimeoutException(string message, Exception innerExc)
            : base(message,innerExc)
        {
            
        }
    }

    public class JsonSamClientSettings
    {
        public TimeSpan CallbackPingTimeout 
            = TimeSpan.FromSeconds(40.0);
        public TimeSpan TotalReturnTimeout 
            = TimeSpan.FromMinutes(5.0f);
        public bool CompressData = false;
    }

    public class JsonSamServiceInterceptor : IInvokeWrapper
    {
        public Subject<bool> ServiceStatus 
            = new Subject<bool>();
        private readonly ReliableSamHelper _reliableSamHelper;
        private class ContractMethodInfo
        {
            public bool CallReliable;
            public bool ReturnResultReliable;
        }
        private readonly Dictionary<string, ContractMethodInfo> _methodsDb =
            new Dictionary<string, ContractMethodInfo>();
        private readonly string _serverDestination;
        private readonly JsonSamClientSettings _samClientSettings;
        private readonly Action<RpcRethrowableException>
            _advancedRpcRethrowableExceptionHandling; 
        public JsonSamServiceInterceptor(
            ReliableSamHelper reliableSamHelper, 
            Type serviceInterface, 
            string serverDestination,
            JsonSamClientSettings samClientSettings,
            Action<RpcRethrowableException> 
                advancedRpcRethrowableExceptionHandling = null 
        )
        {
            _advancedRpcRethrowableExceptionHandling
                = advancedRpcRethrowableExceptionHandling;
            _samClientSettings = samClientSettings;
            _serverDestination = serverDestination;
            _reliableSamHelper = reliableSamHelper;
            foreach (
                MethodInfo methodInfo 
                in JsonRpcClientProcessor.GetPublicMethods(
                    serviceInterface
                )
            )
            {
                var attr = 
                    methodInfo.GetCustomAttribute<
                        JsonSamOperationContractAttribute
                    >() 
                    ?? new JsonSamOperationContractAttribute();
                _methodsDb.Add(
                    methodInfo.Name, 
                    new ContractMethodInfo()
                    {
                        CallReliable = attr.CallReliable,
                        ReturnResultReliable = attr.ReturnResultReliable
                    }
                );
            }
        }

        public void BeforeInvoke(InvocationInfo info)
        {
        }

        public enum DoInvokeImplExcs
        {
            UnexpectedError,
            MethodNotFound,
            ReturnResultError,
            ServerError
        }

        private static readonly Logger _log = LogManager.GetCurrentClassLogger();
        private async Task<object> DoInvokeImpl(InvocationInfo info)
        {
            using (var cts = new CancellationTokenSource())
            {
                try
                {
                    string methodName = info.TargetMethod.Name;
                    if (!_methodsDb.ContainsKey(methodName))
                        throw new EnumException<DoInvokeImplExcs>(
                            DoInvokeImplExcs.MethodNotFound,
                            tag: methodName
                            );
                    var methodInfo = _methodsDb[methodName];
                    uint sendMessageId = await _reliableSamHelper.GetNextOutMessageId().ConfigureAwait(false);
                    var pingCallbackTimeoutTcs = new TaskCompletionSource<object>();
                    IDisposable pingCallbackCheckSubscription =
                        _reliableSamHelper.RawDatagramReceived
                            .Where(
                                x =>
                                    x.ReplyToMessageId == sendMessageId
                                    && x.MessageKind ==
                                    (byte) EJsonRpcSamI2PMessageKinds.ReceiveCallbackPing
                                    && x.Destination == _serverDestination
                            ).Timeout(_samClientSettings.CallbackPingTimeout)
                            .ObserveOn(TaskPoolScheduler.Default)
                            .Subscribe(
                                i => { },
                                exc =>
                                {
                                    if (exc is TimeoutException)
                                    {
                                        pingCallbackTimeoutTcs.TrySetResult(null);
                                    }
                                }
                            );
                    try
                    {
                        Task<ReliableSamHelper.ReliableMessageReceivedArgs> waitResult;
                        if (methodInfo.ReturnResultReliable)
                        {
                            waitResult = _reliableSamHelper.ReliableMessageReceived
                                .Where(
                                    x =>
                                        x.ReplyToMessageId == sendMessageId
                                        && x.MessageKind ==
                                        (byte) EJsonRpcSamI2PMessageKinds.ReceiveResult
                                        && x.Destination == _serverDestination
                                )
                                .FirstAsync()
                                .ToTask(cts.Token);
                        }
                        else
                        {
                            waitResult = _reliableSamHelper.RawDatagramReceived
                                .Where(
                                    x =>
                                        x.ReplyToMessageId == sendMessageId
                                        && x.MessageKind ==
                                        (byte) EJsonRpcSamI2PMessageKinds.ReceiveResult
                                        && x.Destination == _serverDestination
                                )
                                .FirstAsync()
                                .ToTask(cts.Token);
                        }

                        var waitResultErr = _reliableSamHelper.RawDatagramReceived
                            .Where(
                                x =>
                                    x.ReplyToMessageId == sendMessageId
                                    && x.MessageKind ==
                                    (byte) EJsonRpcSamI2PMessageKinds.ReceiveError
                                    && x.Destination == _serverDestination
                            ).FirstAsync()
                            .ToTask(cts.Token);

                        var joe = JsonRpcClientProcessor.GetJsonRpcRequest(info);
                        string jsonRequest = JsonConvert.SerializeObject(joe, Formatting.Indented);
                        byte[] byteJsonRequest = Encoding.UTF8.GetBytes(jsonRequest);
                        if (_samClientSettings.CompressData)
                            byteJsonRequest = byteJsonRequest.Compress();
                        try
                        {
                            if (methodInfo.CallReliable)
                            {
                                await _reliableSamHelper.SendReliableMessage(
                                    _serverDestination,
                                    byteJsonRequest,
                                    messageId:
                                        sendMessageId,
                                    messageKind:
                                        (byte) EJsonRpcSamI2PMessageKinds.SendCall,
                                    token: cts.Token
                                    ).ConfigureAwait(false);
                            }
                            else
                            {
                                await _reliableSamHelper.SendRawDatagram(
                                    _serverDestination,
                                    byteJsonRequest,
                                    sendMessageId,
                                    (byte) EJsonRpcSamI2PMessageKinds.SendCall
                                    ).ConfigureAwait(false);
                            }
                        }
                        catch (
                            EnumException<
                                ReliableSamHelper.SendReliableMessageExcs
                                > reliableExc
                            )
                        {
                            if (
                                reliableExc.ExceptionCode
                                == ReliableSamHelper
                                    .SendReliableMessageExcs
                                    .HandshakeTimeout
                                ||
                                reliableExc.ExceptionCode
                                == ReliableSamHelper
                                    .SendReliableMessageExcs
                                    .SendBlocksTimeout
                                )
                            {
                                ServiceStatus.OnNext(false);
                                throw new I2PTimeoutException(
                                    "Send rpc request i2p error",
                                    reliableExc
                                    );
                            }
                            throw;
                        }
                        var delyOrResultTask = await Task.WhenAny(
                            waitResult,
                            waitResultErr,
                            Task.Delay(
                                _samClientSettings.TotalReturnTimeout,
                                cts.Token
                                ),
                            pingCallbackTimeoutTcs.Task
                            ).ConfigureAwait(false);
                        if (delyOrResultTask != waitResult)
                        {
                            if (delyOrResultTask == waitResultErr)
                            {
                                var errResult = await waitResultErr.ConfigureAwait(false);
                                ServiceStatus.OnNext(true);
                                var errResultData = errResult.Data;
                                if (_samClientSettings.CompressData)
                                    errResultData = errResultData.Decompress();
                                var jsonResult
                                    = JsonConvert.DeserializeObject<JObject>(
                                        Encoding.UTF8.GetString(errResultData)
                                        );
                                return await JsonRpcClientProcessor.GetJsonRpcResult(
                                    jsonResult,
                                    info
                                    ).ConfigureAwait(false);
                            }
                            ServiceStatus.OnNext(false);
                            if (delyOrResultTask == pingCallbackTimeoutTcs.Task)
                            {
                                throw new I2PTimeoutException(
                                    "Callback ping timeout",
                                    null
                                    );
                            }
                            throw new I2PTimeoutException(
                                "Get result timeout",
                                null
                                );
                        }
                        {
                            var retArgs = await waitResult.ConfigureAwait(false);
                            ServiceStatus.OnNext(true);
                            var retArgsData = retArgs.Data;
                            if (_samClientSettings.CompressData)
                                retArgsData = retArgsData.Decompress();
                            var jsonResult = JsonConvert.DeserializeObject<JObject>(
                                Encoding.UTF8.GetString(retArgsData)
                                );
                            return await JsonRpcClientProcessor.GetJsonRpcResult(jsonResult, info)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        pingCallbackCheckSubscription.Dispose();
                    }
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (RpcRethrowableException rpcExc)
                {
                    if (_advancedRpcRethrowableExceptionHandling != null)
                        _advancedRpcRethrowableExceptionHandling(rpcExc);
                    throw;
                }
                catch (EnumException<DoInvokeImplExcs>)
                {
                    throw;
                }
                catch (Exception exc)
                {
                    throw new EnumException<DoInvokeImplExcs>(
                        DoInvokeImplExcs.UnexpectedError,
                        innerException: exc
                        );
                }
                finally
                {
                    cts.Cancel();
                }
            }
        }

        public object DoInvoke(InvocationInfo info)
        {
            return JsonRpcClientProcessor.DoInvokeHelper(
                info,
                DoInvokeImpl
            );
        }

        public void AfterInvoke(InvocationInfo info, object returnValue)
        {
        }

        public static T1 GetClientProxy<T1>(
            ReliableSamHelper helper, 
            string serverDestination,
            out JsonSamServiceInterceptor interceptor,
            JsonSamClientSettings jsonSamClientSettings,
            Action<RpcRethrowableException> 
                advancedRpcRethrowableExceptionHandling = null 
            )
        {
            if (helper == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => helper));
            if (string.IsNullOrWhiteSpace(serverDestination))
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(()=>serverDestination));
            var factory = new ProxyFactory();
            interceptor = new JsonSamServiceInterceptor(
                helper, 
                typeof(T1), 
                serverDestination,
                jsonSamClientSettings, 
                advancedRpcRethrowableExceptionHandling
            );
            return factory.CreateProxy<T1>(interceptor);
        }
    }
    public class SamClientInfo<T1> : IMyAsyncDisposable
    {
        public ISamHelper SamHelperSend;
        public ReliableSamHelper ReliableHelperSend;
        public JsonSamServiceInterceptor SamInterceptor;
        public T1 JsonClient;
        /**/
        private static readonly Logger _log
            = LogManager.GetCurrentClassLogger();
        /**/
        public static async Task<SamClientInfo<T1>> CreateInstance(
            string samServerAddress,
            int samServerPort,
            string serverDestination,
            CancellationToken cancellationToken,
            JsonSamClientSettings jsonSamClientSettings,
            string clientPrivateKeys = null,
            Action<RpcRethrowableException> 
                advancedRpcRethrowableExceptionHandling = null,
            bool useReconnectingSamHelper = false,
            string i2CpOptions = null
        )
        {
            var result = new SamClientInfo<T1>();
            ISamHelper samHelperSend;
            var samHelperSettings = new SamHelperSettings()
            {
                SamServerAddress = samServerAddress,
                SamServerPort = samServerPort,
                SessionPrivateKeys = clientPrivateKeys,
                I2CpOptions = i2CpOptions
            };
            if (!useReconnectingSamHelper)
            {
                samHelperSend = await SamHelper.SamHelper.CreateInstance(
                    samHelperSettings,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            else
            {
                samHelperSend = await ReconnectingSamHelper.CreateInstance(
                    new ReconnectingSamHelperSettings()
                    {
                        ImplementationHelperSettings = samHelperSettings
                    },
                    cancellationToken
                ).ConfigureAwait(false);
            }
            try
            {
                result.SamHelperSend = samHelperSend;
                var reliableHelperSend = new ReliableSamHelper(
                    samHelperSend,
                    new ReliableSamHelperSettings()
                    );
                result.ReliableHelperSend = reliableHelperSend;
                result.JsonClient =
                    JsonSamServiceInterceptor.GetClientProxy<T1>(
                        reliableHelperSend,
                        serverDestination,
                        out result.SamInterceptor,
                        jsonSamClientSettings,
                        advancedRpcRethrowableExceptionHandling
                    );
            }
			catch(Exception)
            {
                await samHelperSend.MyDisposeAsync().ConfigureAwait(false);
                throw;
            }
            return result;
        }

        private bool _disposed = false;
        public async Task MyDisposeAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException("SamClientInfo");
            await ReliableHelperSend.MyDisposeAsync().ConfigureAwait(false);
            await SamHelperSend.MyDisposeAsync().ConfigureAwait(false);
            _disposed = true;
        }
    }
}
