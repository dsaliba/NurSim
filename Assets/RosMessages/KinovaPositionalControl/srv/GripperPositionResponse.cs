//Do not edit! This file was generated by Unity-ROS MessageGeneration.
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.KinovaPositionalControl
{
    [Serializable]
    public class GripperPositionResponse : Message
    {
        public const string k_RosMessageName = "kinova_positional_control/GripperPosition";
        public override string RosMessageName => k_RosMessageName;

        public bool state;

        public GripperPositionResponse()
        {
            this.state = false;
        }

        public GripperPositionResponse(bool state)
        {
            this.state = state;
        }

        public static GripperPositionResponse Deserialize(MessageDeserializer deserializer) => new GripperPositionResponse(deserializer);

        private GripperPositionResponse(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.state);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.state);
        }

        public override string ToString()
        {
            return "GripperPositionResponse: " +
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
