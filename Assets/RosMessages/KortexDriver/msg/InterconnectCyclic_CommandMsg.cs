//Do not edit! This file was generated by Unity-ROS MessageGeneration.
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.KortexDriver
{
    [Serializable]
    public class InterconnectCyclic_CommandMsg : Message
    {
        public const string k_RosMessageName = "kortex_driver/InterconnectCyclic_Command";
        public override string RosMessageName => k_RosMessageName;

        public InterconnectCyclic_MessageIdMsg command_id;
        public uint flags;
        public InterconnectCyclic_Command_tool_commandMsg oneof_tool_command;

        public InterconnectCyclic_CommandMsg()
        {
            this.command_id = new InterconnectCyclic_MessageIdMsg();
            this.flags = 0;
            this.oneof_tool_command = new InterconnectCyclic_Command_tool_commandMsg();
        }

        public InterconnectCyclic_CommandMsg(InterconnectCyclic_MessageIdMsg command_id, uint flags, InterconnectCyclic_Command_tool_commandMsg oneof_tool_command)
        {
            this.command_id = command_id;
            this.flags = flags;
            this.oneof_tool_command = oneof_tool_command;
        }

        public static InterconnectCyclic_CommandMsg Deserialize(MessageDeserializer deserializer) => new InterconnectCyclic_CommandMsg(deserializer);

        private InterconnectCyclic_CommandMsg(MessageDeserializer deserializer)
        {
            this.command_id = InterconnectCyclic_MessageIdMsg.Deserialize(deserializer);
            deserializer.Read(out this.flags);
            this.oneof_tool_command = InterconnectCyclic_Command_tool_commandMsg.Deserialize(deserializer);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.command_id);
            serializer.Write(this.flags);
            serializer.Write(this.oneof_tool_command);
        }

        public override string ToString()
        {
            return "InterconnectCyclic_CommandMsg: " +
            "\ncommand_id: " + command_id.ToString() +
            "\nflags: " + flags.ToString() +
            "\noneof_tool_command: " + oneof_tool_command.ToString();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod]
#endif
        public static void Register()
        {
            MessageRegistry.Register(k_RosMessageName, Deserialize);
        }
    }
}
