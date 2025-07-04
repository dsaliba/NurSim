//Do not edit! This file was generated by Unity-ROS MessageGeneration.
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.GopherRosClearcore
{
    [Serializable]
    public class StopResponse : Message
    {
        public const string k_RosMessageName = "gopher_ros_clearcore/Stop";
        public override string RosMessageName => k_RosMessageName;

        public bool state;

        public StopResponse()
        {
            this.state = false;
        }

        public StopResponse(bool state)
        {
            this.state = state;
        }

        public static StopResponse Deserialize(MessageDeserializer deserializer) => new StopResponse(deserializer);

        private StopResponse(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.state);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.state);
        }

        public override string ToString()
        {
            return "StopResponse: " +
            "\nstate: " + state.ToString();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod]
#endif
        public static void Register()
        {
            MessageRegistry.Register(k_RosMessageName, Deserialize, MessageSubtopic.Response);
        }
    }
}
