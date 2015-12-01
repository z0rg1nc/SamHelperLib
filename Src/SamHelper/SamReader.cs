using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtils;
using NLog;
using ObjectStateLib;

namespace BtmI2p.SamHelper
{
    public class DestReplyReceivedArgs
    {
        public string PublicKey = string.Empty;
        public string PrivateKey = string.Empty;
    }

    public class HelloReplyReceivedArgs
    {
        public bool Ok = false;
        public string ErrorMessage = string.Empty;
        public string Version = string.Empty;
    }

    public class NamingReplyReceivedArgs
    {
        public string Name = string.Empty;
        public string Result = string.Empty;
        public string ValueString = string.Empty;
        public string Message = string.Empty;
    }

    public class SessionStatusReceivedArgs
    {
        public string SessionStatusResult = string.Empty;
        public string DestinationWithPrivateKey = string.Empty;
        public string Message = string.Empty;
    }

    public class StreamClosedReceivedArgs
    {
        public string Result = string.Empty;
        public int Id = 0;
        public string Message = string.Empty;
    }

    public class StreamConnectedReceivedArgs
    {
        public string RemoteDestination = string.Empty;
        public int Id = 0;
    }

    public class StreamDataReceivedArgs
    {
        public int Id = 0;
        public byte[] Data = new byte[0];
        public int Offset = 0;
        public int Length = 0;
    }

    public class DatagramDataReceivedArgs
    {
        public string Destination = string.Empty;
        public byte[] Data = new byte[0];
    }

    public class StreamStatusReceivedArgs
    {
        public string Result = string.Empty;
        public int Id = 0;
        public string Message = string.Empty;
    }

    public class UnknownMessageReceivedArgs
    {
        public UnknownMessageReceivedArgs(
            string major, 
            string minor, 
            NameValueCollection parameters
        )
        {
            Major = major;
            Minor = minor;
            Parameters = parameters;
        }
        public string Major = string.Empty;
        public string Minor = string.Empty;
        public NameValueCollection Parameters = new NameValueCollection();
    }
    
	/// <summary>
	///   Reads from a socket stream, producing events for any SAM message read.
	/// </summary>
	public class SamReader : IMyAsyncDisposable
	{
		public Subject<DestReplyReceivedArgs> DestReplyReceived 
            = new Subject<DestReplyReceivedArgs>();
        public Subject<HelloReplyReceivedArgs> HelloReplyReceived 
            = new Subject<HelloReplyReceivedArgs>();
        public Subject<NamingReplyReceivedArgs> NamingReplyReceived 
            = new Subject<NamingReplyReceivedArgs>();
        public Subject<SessionStatusReceivedArgs> SessionStatusReceived 
            = new Subject<SessionStatusReceivedArgs>();
        public Subject<DatagramDataReceivedArgs> DatagramDataReceived 
            = new Subject<DatagramDataReceivedArgs>();
        public Subject<UnknownMessageReceivedArgs> UnknownMessageReceived 
            = new Subject<UnknownMessageReceivedArgs>();

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public readonly Subject<object> IoExceptionThrown 
            = new Subject<object>(); 
	    private class NetWorkHelper : IMyAsyncDisposable
	    {
            private readonly NetworkStream _samStream;
            private const int BufferSize = 32768;
            private readonly byte[] _buffer 
                = new byte[BufferSize];
            private readonly List<byte> _accumulateBuffer 
                = new List<byte>();

	        private string AccumulatedString
	        {
	            get
	            {
	                if (_accumulateBuffer.Count == 0)
	                    return null;
	                int sInd = _accumulateBuffer.FindIndex(x => x == (byte) '\n');
	                if (sInd == -1)
	                    return null;
	                if (sInd == 0)
	                {
                        _accumulateBuffer.RemoveAt(0);
                        return string.Empty;
	                }
	                byte[] resultBytes = _accumulateBuffer.Take(sInd).ToArray();
	                _accumulateBuffer.RemoveRange(0, sInd + 1);
	                var result = Encoding.ASCII.GetString(resultBytes, 0, sInd);
	                return result;
	            }
	        }
            private readonly SemaphoreSlim _streamReadLockSem
                = new SemaphoreSlim(1);
            private async Task ReadAndAccumulate(
                CancellationToken cancellationToken
            )
            {
                int readBytes;
                using (await _streamReadLockSem.GetDisposable().ConfigureAwait(false))
                {
                    readBytes = await _samStream.ReadAsync(
                        _buffer,
                        0,
                        BufferSize,
                        cancellationToken
                    ).ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
                }
                if (readBytes > 0)
	                _accumulateBuffer.AddRange(_buffer.Take(readBytes));
	        }
            public async Task<string> ReadLine(
                CancellationToken cancellationToken
            )
            {
                var curMethodName = this.MyNameOfMethod(
                    e => e.ReadLine(CancellationToken.None)
                );
                using (_stateHelper.GetFuncWrapper())
                {
                    var res = AccumulatedString;
                    while (res == null)
                    {
                        try
                        {
                            await ReadAndAccumulate(
                                cancellationToken
                            ).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.Trace("ReadLine cancelled");
                            throw;
                        }
                        res = AccumulatedString;
                    }
                    return res;
                }
	        }

            public async Task<byte[]> ReadBytes(
                int count, 
                CancellationToken cancellationToken
            )
	        {
                var curMethodName = this.MyNameOfMethod(
                    e => e.ReadBytes(0,CancellationToken.None)
                );
                using (_stateHelper.GetFuncWrapper())
                {
                    while (_accumulateBuffer.Count < count)
                    {
                        try
                        {
                            await ReadAndAccumulate(
                                cancellationToken
                            ).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.Trace(
                                "ReadBytes ReadAndAccumulate cancelled"
                            );
                            throw;
                        }
                    }
                    var result = _accumulateBuffer.Take(count).ToArray();
                    _accumulateBuffer.RemoveRange(0, count);
                    return result;
                }
	        }
	        public NetWorkHelper(
                NetworkStream stream
            )
	        {
	            _samStream = stream;
                _stateHelper.SetInitializedState();
	        }
            private readonly DisposableObjectStateHelper _stateHelper
                = new DisposableObjectStateHelper("NetworkHelper");

	        public async Task MyDisposeAsync()
	        {
                await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
	        }
	    }

	    private readonly NetWorkHelper _netHelper;
        public SamReader(NetworkStream samStream)
        {
            _netHelper = new NetWorkHelper(samStream);
            _stateHelper.SetInitializedState();
            RunThreadImpl();
        }

	    private static readonly Logger _logger 
            = LogManager.GetCurrentClassLogger();
	    private async void RunThreadImpl()
	    {
	        var curMethodName = this.MyNameOfMethod(e => e.RunThreadImpl());
	        try
	        {
                _logger.Trace("Start reading");
	            while (!_cts.Token.IsCancellationRequested)
	            {
                    using (_stateHelper.GetFuncWrapper())
                    {
	                    var parameters = new NameValueCollection();
	                    string line;
	                    try
	                    {
	                        line = await _netHelper.ReadLine(
	                            _cts.Token
	                        ).ConfigureAwait(false);
	                    }
	                    catch (IOException)
	                    {
	                        IoExceptionThrown.OnNext(null);
	                        return;
	                    }
	                    string[] tokens = line.Split(new char[] {' '});

	                    if (tokens.Length < 2)
	                    {
	                        _logger.Error(
	                            "Invalid SAM line: [{0}]",
	                            line
	                            );
	                        _cts.Cancel(false);
	                        return;
	                    }

	                    IEnumerator tokensEnumerator = tokens.GetEnumerator();
	                    tokensEnumerator.MoveNext();
	                    var major = (string) tokensEnumerator.Current;
	                    tokensEnumerator.MoveNext();
	                    var minor = (string) tokensEnumerator.Current;

	                    parameters.Clear();

	                    while (tokensEnumerator.MoveNext())
	                    {
	                        var pair = (string) tokensEnumerator.Current;
	                        int equalsPosition = pair.IndexOf('=');

	                        if (
	                            (equalsPosition > 0)
	                            && (equalsPosition < pair.Length - 1)
	                            )
	                        {

	                            string name = pair.Substring(
	                                0,
	                                equalsPosition
	                                );
	                            string valueString = pair.Substring(
	                                equalsPosition + 1
	                                );

	                            while (
	                                (valueString[0] == '\"')
	                                && (valueString.Length > 0)
	                                )
	                                valueString = valueString.Substring(1);

	                            while (
	                                (valueString.Length > 0)
	                                && (valueString[valueString.Length - 1] == '\"')
	                                )
	                                valueString = valueString.Substring(
	                                    0,
	                                    valueString.Length - 1
	                                    );

	                            parameters.Set(name, valueString);
	                        }
	                    }
	                    try
	                    {
	                        await ProcessEvent(major, minor, parameters).ConfigureAwait(false);
	                    }
	                    catch (IOException)
	                    {
	                        _logger.Error(
	                            "{0} {1} IoException",
	                            curMethodName,
	                            this.MyNameOfMethod(e => e.ProcessEvent(null, null, null))
	                            );
	                        IoExceptionThrown.OnNext(null);
	                        return;
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
	            _logger.Error(
	                "{0} unexpected error '{1}'",
	                exc.ToString()
	                );
	        }
	    }
        
		private async Task ProcessEvent(
            string major, 
            string minor, 
            NameValueCollection parameters
        )
		{
		    var curMethodName = this.MyNameOfMethod(e => e.ProcessEvent(null, null, null));
			switch (major)
			{
				case "HELLO" :

					if (minor.Equals("REPLY")) {
						string result = parameters.Get("RESULT");

					    if (result.Equals("OK"))
					    {
					        string version = parameters.Get("VERSION");
					        HelloReplyReceived.OnNext(
                                new HelloReplyReceivedArgs
                                {
                                    Ok = true, 
                                    Version = version
                                }
                            );
					    }
					    else if(result.Equals("NOVERSION"))
					    {
                            HelloReplyReceived.OnNext(
                                new HelloReplyReceivedArgs
                                {
                                    Ok = false, 
                                    Version = "NOVERSION"
                                }
                            );
					    }
                        else if (result.Equals("I2P_ERROR"))
                        {
                            HelloReplyReceived.OnNext(
                                new HelloReplyReceivedArgs
                                {
                                    Ok = false, 
                                    ErrorMessage = parameters.Get("MESSAGE")
                                }
                            );
                        }
                        else
                        {
                            HelloReplyReceived.OnNext(
                                new HelloReplyReceivedArgs
                                {
                                    Ok = false
                                }
                            );
                        }
					} else {
                        UnknownMessageReceived.OnNext(
                            new UnknownMessageReceivedArgs(
                                major, 
                                minor, 
                                parameters
                            )
                        );
					}
					break;
				case "SESSION" :

					if (minor.Equals("STATUS")) {

						string result = parameters.Get("RESULT");
						string destinationWithPrivateKey = parameters.Get("DESTINATION");
						string message = parameters.Get("MESSAGE");

                        SessionStatusReceived.OnNext(
                            new SessionStatusReceivedArgs
                            {
                                SessionStatusResult = result,
                                DestinationWithPrivateKey = destinationWithPrivateKey,
                                Message = message
                            }
                        );
					} else {
                        UnknownMessageReceived.OnNext(
                            new UnknownMessageReceivedArgs(
                                major, 
                                minor, 
                                parameters
                            )
                        );
					}

					break;

                case "DATAGRAM":
			        if (minor.Equals("RECEIVED"))
			        {
			            string destination = parameters.Get("DESTINATION");
                        int sizeValue = Int32.Parse(parameters.Get("SIZE"));
			            byte[] data;
			            try
			            {
			                data = await _netHelper.ReadBytes(
			                    sizeValue,
			                    _cts.Token
                            ).ConfigureAwait(false);
			                // wrong!!!!
			                if (data.Length != sizeValue)
			                {
			                    UnknownMessageReceived.OnNext(
			                        new UnknownMessageReceivedArgs(
			                            major,
			                            minor,
			                            parameters
			                        )
			                    );
			                    return;
			                }
			            }
			            catch (IOException)
			            {
			                throw;
			            }
			            catch (Exception exc)
			            {
			                _logger.Error(exc.Message);
			                _cts.Cancel(false);
			                UnknownMessageReceived.OnNext(
			                    new UnknownMessageReceivedArgs(
			                        major,
			                        minor,
			                        parameters
			                        )
			                    );
			                return;
			            }
			            DatagramDataReceived.OnNext(
                            new DatagramDataReceivedArgs()
                            {
                                Data = data, 
                                Destination = destination
                            }
                        );
			        }
			        break;

				case "NAMING" :

					if (minor.Equals("REPLY")) {

						string name = parameters.Get("NAME");
						string result = parameters.Get("RESULT");
						string valueString = parameters.Get("VALUE");
						string message = parameters.Get("MESSAGE");

                        NamingReplyReceived.OnNext(
                            new NamingReplyReceivedArgs
                            {
                                Message = message,
                                Name = name,
                                Result = result,
                                ValueString = valueString
                            }
                        );
					} else {
                        UnknownMessageReceived.OnNext(
                            new UnknownMessageReceivedArgs(
                                major, 
                                minor, 
                                parameters
                            )
                        );
					}

					break;

				case "DEST" :

					if (minor.Equals("REPLY")) {

						string pub = parameters.Get("PUB");
						string priv = parameters.Get("PRIV");

                        DestReplyReceived.OnNext(
                            new DestReplyReceivedArgs
                            {
                                PrivateKey = priv, 
                                PublicKey = pub
                            });
					} else {
                        UnknownMessageReceived.OnNext(
                            new UnknownMessageReceivedArgs(
                                major, 
                                minor, 
                                parameters
                            )
                        );
					}
                    
					break;

				default :

                    UnknownMessageReceived.OnNext(
                        new UnknownMessageReceivedArgs(
                            major, 
                            minor, 
                            parameters
                        )
                    );
					break;
			}
		}
        private readonly DisposableObjectStateHelper _stateHelper 
            =  new DisposableObjectStateHelper("SamReader");
	    public async Task MyDisposeAsync()
	    {
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            await _netHelper.MyDisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
	    }
	}
}
