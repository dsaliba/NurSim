//Do not edit! This file was generated by Unity-ROS MessageGeneration.
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.GopherRosClearcore
{
    [Serializable]
    public class HomingRequest : Message
    {
        public const string k_RosMessageName = "gopher_ros_clearcore/Homing";
        public override string RosMessageName => k_RosMessageName;

        public bool command;

        public HomingRequest()
        {
            this.command = false;
        }

        public HomingRequest(bool command)
        {
            this.command = command;
        }

        public static HomingRequest Deserialize(MessageDeserializer deserializer) => new HomingRequest(deserializer);

        private HomingRequest(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.command);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.command);
        }

        public override string ToString()
        {
            return "HomingRequest: " +
            "\ncommand: " + command.ToString();
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
