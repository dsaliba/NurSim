//Do not edit! This file was generated by Unity-ROS MessageGeneration.
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.KortexDriver
{
    [Serializable]
    public class MotorCommandMsg : Message
    {
        public const string k_RosMessageName = "kortex_driver/MotorCommand";
        public override string RosMessageName => k_RosMessageName;

        public uint motor_id;
        public float position;
        public float velocity;
        public float force;

        public MotorCommandMsg()
        {
            this.motor_id = 0;
            this.position = 0.0f;
            this.velocity = 0.0f;
            this.force = 0.0f;
        }

        public MotorCommandMsg(uint motor_id, float position, float velocity, float force)
        {
            this.motor_id = motor_id;
            this.position = position;
            this.velocity = velocity;
            this.force = force;
        }

        public static MotorCommandMsg Deserialize(MessageDeserializer deserializer) => new MotorCommandMsg(deserializer);

        private MotorCommandMsg(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.motor_id);
            deserializer.Read(out this.position);
            deserializer.Read(out this.velocity);
            deserializer.Read(out this.force);
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.Write(this.motor_id);
            serializer.Write(this.position);
            serializer.Write(this.velocity);
            serializer.Write(this.force);
        }

        public override string ToString()
        {
            return "MotorCommandMsg: " +
            "\nmotor_id: " + motor_id.ToString() +
            "\nposition: " + position.ToString() +
            "\nvelocity: " + velocity.ToString() +
            "\nforce: " + force.ToString();
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
