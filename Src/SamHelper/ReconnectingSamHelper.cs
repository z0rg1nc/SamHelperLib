using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;
using NLog;

namespace BtmI2p.SamHelper
{
    public class ReconnectingSamHelperSettings : ICheckable
    {
        public ReconnectingSamHelperSettings()
        {
        }
        public ReconnectingSamHelperSettings(
            ReconnectingSamHelperSettings copyFrom)
        {
            TimeoutOnIoException = copyFrom.TimeoutOnIoException;
            DelayOnConnectFailed = copyFrom.DelayOnConnectFailed;
            if(copyFrom.ImplementationHelperSettings != null)
                ImplementationHelperSettings = new SamHelperSettings(
                    copyFrom.ImplementationHelperSettings);
        }

        public TimeSpan TimeoutOnIoException 
            = TimeSpan.FromSeconds(5.0d);

        public TimeSpan DelayOnConnectFailed
            = TimeSpan.FromSeconds(15.0d);

        public SamHelperSettings ImplementationHelperSettings = null;
        public void CheckMe()
        {
            if(TimeoutOnIoException < TimeSpan.Zero
                || TimeoutOnIoException > TimeSpan.FromSeconds(100.0d)
            )
                throw new ArgumentOutOfRangeException(
                    this.MyNameOfProperty(e => e.TimeoutOnIoException));
            if(ImplementationHelperSettings == null)
                throw new ArgumentNullException(
                    this.MyNameOfProperty(e => e.ImplementationHelperSettings));
            ImplementationHelperSettings.CheckMe();
        }
    }
    public class ReconnectingSamHelper : ISamHelper
    {
        private readonly CancellationTokenSource _cts
            = new CancellationTokenSource();
        private readonly DisposableObjectStateHelper _stateHelper
            = new DisposableObjectStateHelper("ReconnectingSamHelper");
        public async Task MyDisposeAsync()
        {
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            if (_currentImplementationHelper != null)
                await _currentImplementationHelper.MyDisposeAsync().ConfigureAwait(false);
            foreach (var subscription in _currentImplSubscriptions)
            {
                subscription.Dispose();
            }
            _currentImplSubscriptions.Clear();
            _cts.Dispose();
        }
        private ReconnectingSamHelper()
        {
        }
        private readonly SemaphoreSlim _tryToReconnectLockSem 
            = new SemaphoreSlim(1);

        private DateTime _lastHelperRenewedTime = DateTime.MinValue;
        public async void TryToReconnect(DateTime callTime)
        {
            var curMethodName = this.MyNameOfMethod(e => e.TryToReconnect(DateTime.MinValue));
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    if (callTime < _lastHelperRenewedTime)
                    {
                        _log.Trace("{0} callTime < _lastHelperRenewedTime", curMethodName);
                        return;
                    }
                    using (await _tryToReconnectLockSem.GetDisposable(true).ConfigureAwait(false))
                    {
LBL_RETRY:
                        if(_cts.IsCancellationRequested)
                            return;
                        _log.Trace("{0} called", curMethodName);
                        var curImpl = _currentImplementationHelper;
                        if (curImpl != null)
                        {
                            _currentImplementationHelper = null;
                            await curImpl.MyDisposeAsync().ConfigureAwait(false);
                            foreach (var subscription in _currentImplSubscriptions)
                            {
                                subscription.Dispose();
                            }
                            _currentImplSubscriptions.Clear();
                        }
                        bool excThrown = false;
                        try
                        {
                            await InitImpl(_cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exc)
                        {
                            _log.Error(
                                "{0} InitImpl err '{1}'",
                                curMethodName,
                                exc.Message
                                );
                            excThrown = true;
                        }
                        if (excThrown)
                        {
                            await Task.Delay(_settings.DelayOnConnectFailed,_cts.Token).ConfigureAwait(false);
                            goto LBL_RETRY;
                        }
                    }
                }
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exc)
            {
                _log.Error(
                    "{0} unexpected error '{1}'", 
                    curMethodName, 
                    exc.ToString()
                );
            }
        }

        private async Task InitImpl(CancellationToken token)
        {
            _currentImplementationHelper = await SamHelper.CreateInstance(
                _settings.ImplementationHelperSettings,
                token
            ).ConfigureAwait(false);
            _currentImplSubscriptions.Add(
                _currentImplementationHelper.DatagramDataReceived
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(
                    x =>
                    {
                        if(_handleDatagrams)
                            _datagramDataReceived.OnNext(x);
                    }
                )
            );
            _currentImplSubscriptions.Add(
                _currentImplementationHelper.IoExceptionThrown
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(
                    i => TryToReconnect(DateTime.UtcNow)
                )
            );
            _lastHelperRenewedTime = DateTime.UtcNow;
        }

        private ReconnectingSamHelperSettings _settings;
        public async static Task<ReconnectingSamHelper> CreateInstance(
            ReconnectingSamHelperSettings settings,
            CancellationToken token
        )
        {
            if(settings == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => settings));
            settings.CheckMe();
            var result = new ReconnectingSamHelper();
            result._settings = settings;
            await result.InitImpl(token).ConfigureAwait(false);
            result._session = result._currentImplementationHelper.Session;
            if (
                string.IsNullOrWhiteSpace(
                    settings.ImplementationHelperSettings.SessionPrivateKeys
                )
            )
                settings.ImplementationHelperSettings.SessionPrivateKeys 
                    = result._session.PrivateKey;
            result._stateHelper.SetInitializedState();
            return result;
        }
        private readonly List<IDisposable> _currentImplSubscriptions 
            = new List<IDisposable>(); 
        private SamHelper _currentImplementationHelper;
        private SamHelper.SamSession _session;
        public bool IsAvailable
        {
            get { return _currentImplementationHelper != null; }
        }
        public SamHelper.SamSession Session {
            get { return _session; }
        }
        private readonly Subject<DatagramDataReceivedArgs> _datagramDataReceived
            = new Subject<DatagramDataReceivedArgs>();
        public Subject<DatagramDataReceivedArgs> DatagramDataReceived
        {
            get { 
                using(_stateHelper.GetFuncWrapper())
                    return _datagramDataReceived; 
            }
        }
        private static readonly Logger _log 
            = LogManager.GetCurrentClassLogger();
        public async Task SendDatagram(string destination, byte[] data)
        {
            var curMethodName = this.MyNameOfMethod(e => e.SendDatagram(null, null));
            using (_stateHelper.GetFuncWrapper())
            {
                if(string.IsNullOrWhiteSpace(destination))
                    throw new ArgumentNullException(
                        MyNameof.GetLocalVarName(() => destination));
                if(data == null)
                    throw new ArgumentNullException(
                        MyNameof.GetLocalVarName(() => data));
                var curImpl = _currentImplementationHelper;
                bool throwTimeoutExc = false;
                if (curImpl == null)
                {
                    //_log.Error("{0} curImpl == null", curMethodName);
                    throwTimeoutExc = true;
                }
                else
                {
                    try
                    {
                        await curImpl.SendDatagram(destination, data).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        _log.Error("{0} IOException", curMethodName);
                        throwTimeoutExc = true;
                        TryToReconnect(DateTime.UtcNow);
                    }
                    catch (WrongDisposableObjectStateException)
                    {
                        _log.Error("{0} WrongDisposableState", curMethodName);
                        throwTimeoutExc = true;
                    }
                }
                if (throwTimeoutExc)
                {
                    await Task.Delay(_settings.TimeoutOnIoException).ConfigureAwait(false);
                    throw new TimeoutException();
                }
            }
        }

        private bool _handleDatagrams = true;
        public bool HandleDatagrams
        {
            get
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    return _handleDatagrams;
                }
            }
            set
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    _handleDatagrams = value;
                }
            }
        }
    }
}
