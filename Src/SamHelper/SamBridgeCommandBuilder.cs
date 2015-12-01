using System;
using System.Linq;
using System.Text;

namespace BtmI2p.SamHelper
{
    public static class SamBridgeCommandBuilder
    {
        public static byte[] HelloReply()
        {
            return Encoding.ASCII.GetBytes("HELLO VERSION MIN=3.0 MAX=3.0\n");
        }
        public enum SessionStyle
        {
            Stream,
            Datagram,
            Raw
        }
        public static byte[] SessionCreate(
            SessionStyle sessionStyle, 
            string id, 
            string destinationPrivateKey = null,
            string i2CpOptions = null
        )
        {
            if(id == null)
                throw new ArgumentNullException("id");
            if(id.Contains(' '))
                throw new ArgumentOutOfRangeException("id");
            string sessionTypeString = sessionStyle == SessionStyle.Stream
                ? "STREAM"
                : sessionStyle == SessionStyle.Datagram
                    ? "DATAGRAM"
                    : "RAW";
            destinationPrivateKey = destinationPrivateKey ?? "TRANSIENT";
            string i2cpOptionsParam = i2CpOptions == null ? string.Empty : (" " + i2CpOptions);
            return Encoding.ASCII.GetBytes(string.Format(
                "SESSION CREATE" +
                " STYLE={0}" +
                " ID={1}" +
                " DESTINATION={2}" +
                "{3}" +
                " \n",
                sessionTypeString,
                id,
                destinationPrivateKey,
                i2cpOptionsParam
            ));
        }

        public static byte[] DatagramSend(string destination, byte[] data)
        {
            int dataSize = data.Length;
            string commandString = string.Format("DATAGRAM SEND DESTINATION={0} SIZE={1}\n", destination, dataSize);
            var commandData = Encoding.ASCII.GetBytes(commandString);
            bool isLenEquals = commandString.Length == commandData.Length;
            if(!isLenEquals)
                throw new Exception();
            return MiscUtils.MiscFuncs.ConcatArrays(commandData, data);
        }
    }
}
