﻿// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Edge.Hub.Mqtt.Test
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Hub.Core;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Cloud;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Device;
    using Microsoft.Azure.Devices.Edge.Util.Test.Common;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Azure.Devices.ProtocolGateway.Messaging;
    using Moq;
    using Xunit;
    using IMessage = Microsoft.Azure.Devices.Edge.Hub.Core.IMessage;
    using IProtocolGatewayMessage = ProtocolGateway.Messaging.IMessage;

    [Unit]
    public class MessagingServiceClientTest
    {
        static readonly Mock<IIdentity> Identity = new Mock<IIdentity>();
        static readonly Mock<IMessagingChannel<IProtocolGatewayMessage>> Channel = new Mock<IMessagingChannel<IProtocolGatewayMessage>>();
        static readonly Mock<IEdgeHub> EdgeHub = new Mock<IEdgeHub>();
        static readonly Mock<IConnectionManager> ConnectionManager = new Mock<IConnectionManager>();
        static readonly IList<string> Input = new List<string>() { "devices/{deviceId}/messages/events/" };
        static readonly IList<string> Output = new List<string>() { "devices/{deviceId}/messages/devicebound" };

        struct Messages
        {
            public readonly ProtocolGatewayMessage Source;
            public readonly MqttMessage Expected;

            public Messages(string address, byte[] payload)
            {
                this.Source = new ProtocolGatewayMessage(payload.ToByteBuffer(), address);
                this.Expected = new MqttMessage.Builder(payload).Build();
            }
        }

        static Messages MakeMessages(string address = "dontcare")
        {
            byte[] payload = Encoding.ASCII.GetBytes("abc");
            return new Messages(address, payload);
        }

        static Mock<IDeviceListener> MakeDeviceListenerSpy()
        {
            var listener = new Mock<IDeviceListener>();
            listener.Setup(x => x.ProcessMessageAsync(It.IsAny<IMessage>()))
                .Returns(Task.CompletedTask);
            listener.Setup(x => x.GetTwinAsync())
                .Returns(Task.FromResult(new Twin()));
            return listener;
        }

        static ProtocolGatewayMessageConverter MakeProtocolGatewayMessageConverter()
        {
            var config = new MessageAddressConversionConfiguration(Input, Output);
            var converter = new MessageAddressConverter(config);
            return new ProtocolGatewayMessageConverter(converter);
        }

        [Fact]
        public void ConstructorRequiresADeviceListener()
        {
            var converter = Mock.Of<IMessageConverter<IProtocolGatewayMessage>>();

            Assert.Throws(typeof(ArgumentNullException),
                () => new MessagingServiceClient(null, converter));
        }

        [Fact]
        public void ConstructorRequiresAMessageConverter()
        {
            var listener = Mock.Of<IDeviceListener>();

            Assert.Throws(typeof(ArgumentNullException),
                () => new MessagingServiceClient(listener, null));
        }

        [Fact]
        public async Task SendAsyncThrowsIfMessageAddressIsNullOrWhiteSpace()
        {
            var message = new ProtocolGatewayMessage(new byte[] { 0 }.ToByteBuffer(), null);
            var listener = Mock.Of<IDeviceListener>();
            var converter = Mock.Of<IMessageConverter<IProtocolGatewayMessage>>();

            var client = new MessagingServiceClient(listener, converter);

            await Assert.ThrowsAsync(typeof(ArgumentException),
                () => client.SendAsync(message));
        }

        [Fact]
        public async Task ForwardsMessagesToTheDeviceListener()
        {
            Messages m = MakeMessages();
            Mock<IDeviceListener> listener = MakeDeviceListenerSpy();

            var client = new MessagingServiceClient(listener.Object, MakeProtocolGatewayMessageConverter());
            await client.SendAsync(m.Source);

            listener.Verify(
                x => x.ProcessMessageAsync(It.Is((IMessage actual) => actual.Equals(m.Expected))),
                Times.Once);
        }

        [Fact]
        public async Task RecognizesAGetTwinMessage()
        {
            var message = new ProtocolGatewayMessage(new byte[0].ToByteBuffer(), "$iothub/twin/GET/?$rid=123");
            Mock<IDeviceListener> listener = MakeDeviceListenerSpy();

            var client = new MessagingServiceClient(listener.Object, MakeProtocolGatewayMessageConverter());
            await client.SendAsync(message);

            listener.Verify(x => x.ProcessMessageAsync(It.IsAny<IMessage>()), Times.Never);
            listener.Verify(x => x.GetTwinAsync(), Times.Once);
        }

        [Fact]
        public async Task DoesNotProcessATwinMessageWithASubresource()
        {
            var message = new ProtocolGatewayMessage(new byte[0].ToByteBuffer(), "$iothub/twin/GET/something");
            var listener = new Mock<IDeviceListener>(MockBehavior.Strict);

            var client = new MessagingServiceClient(listener.Object, MakeProtocolGatewayMessageConverter());

            await Assert.ThrowsAsync(typeof(InvalidOperationException),
                () => client.SendAsync(message));
        }

        [Fact]
        public async Task DoesNotProcessATwinMessageWithoutACorrelationId()
        {
            var message = new ProtocolGatewayMessage(new byte[0].ToByteBuffer(), "$iothub/twin/GET/");
            var listener = new Mock<IDeviceListener>(MockBehavior.Strict);

            var client = new MessagingServiceClient(listener.Object, MakeProtocolGatewayMessageConverter());

            await Assert.ThrowsAsync(typeof(InvalidOperationException),
                () => client.SendAsync(message));
        }

        [Fact]
        public async Task DoesNotProcessAnUnsupportedTwinMessage()
        {
            var message = new ProtocolGatewayMessage(new byte[0].ToByteBuffer(), "$iothub/twin/PATCH/properties/reported/");
            var listener = new Mock<IDeviceListener>(MockBehavior.Strict);

            var client = new MessagingServiceClient(listener.Object, MakeProtocolGatewayMessageConverter());

            await Assert.ThrowsAsync(typeof(InvalidOperationException),
                () => client.SendAsync(message));
        }

        [Fact]
        public async Task DoesNotProcessAnInvalidTwinMessage()
        {
            var message = new ProtocolGatewayMessage(new byte[0].ToByteBuffer(), "$iothub/unknown");
            var listener = new Mock<IDeviceListener>(MockBehavior.Strict);

            var client = new MessagingServiceClient(listener.Object, MakeProtocolGatewayMessageConverter());

            await Assert.ThrowsAsync(typeof(InvalidOperationException),
                () => client.SendAsync(message));
        }

        [Fact]
        public async Task TestReceiveMessagingChannelComplete()
        {
            IProtocolGatewayMessage msg = null;

            ProtocolGatewayMessageConverter messageConverter = MakeProtocolGatewayMessageConverter();
            var dp = new DeviceProxy(Channel.Object, Identity.Object, messageConverter);

            var cloudProxy = new Mock<ICloudProxy>();
            cloudProxy.Setup(d => d.SendFeedbackMessageAsync(It.IsAny<IFeedbackMessage>())).Callback<IFeedbackMessage>(
                m =>
                {
                    Assert.Equal(m.FeedbackStatus, FeedbackStatus.Complete);
                });
            cloudProxy.Setup(d => d.BindCloudListener(It.IsAny<ICloudListener>()));

            var deviceListner = new DeviceListener(Identity.Object, EdgeHub.Object, ConnectionManager.Object, cloudProxy.Object);
            var messagingServiceClient = new Mqtt.MessagingServiceClient(deviceListner, messageConverter);

            Channel.Setup(r => r.Handle(It.IsAny<IProtocolGatewayMessage>()))
                .Callback<IProtocolGatewayMessage>(
                    m =>
                    {
                        msg = m;
                        messagingServiceClient.CompleteAsync(msg.Id);
                    });

            messagingServiceClient.BindMessagingChannel(Channel.Object);
            Core.IMessage message = new MqttMessage.Builder(new byte[] { 1, 2, 3 }).Build();
            await dp.SendMessageAsync(message);

            Assert.NotNull(msg);
        }

        [Fact]
        public async Task TestReceiveMessagingChannelReject()
        {
            IProtocolGatewayMessage msg = null;

            ProtocolGatewayMessageConverter messageConverter = MakeProtocolGatewayMessageConverter();
            var dp = new DeviceProxy(Channel.Object, Identity.Object, messageConverter);
            var cloudProxy = new Mock<ICloudProxy>();
            cloudProxy.Setup(d => d.SendFeedbackMessageAsync(It.IsAny<IFeedbackMessage>())).Callback<IFeedbackMessage>(
                m =>
                {
                    Assert.Equal(m.FeedbackStatus, FeedbackStatus.Reject);
                });
            cloudProxy.Setup(d => d.BindCloudListener(It.IsAny<ICloudListener>()));
            var deviceListner = new DeviceListener(Identity.Object, EdgeHub.Object, ConnectionManager.Object, cloudProxy.Object);
            var messagingServiceClient = new Mqtt.MessagingServiceClient(deviceListner, messageConverter);

            Channel.Setup(r => r.Handle(It.IsAny<IProtocolGatewayMessage>()))
                .Callback<IProtocolGatewayMessage>(
                    m =>
                    {
                        msg = m;
                        messagingServiceClient.RejectAsync(msg.Id);
                    });

            messagingServiceClient.BindMessagingChannel(Channel.Object);
            Core.IMessage message = new MqttMessage.Builder(new byte[] { 1, 2, 3 }).Build();
            await dp.SendMessageAsync(message);

            Assert.NotNull(msg);
        }

        [Fact]
        public async Task TestReceiveMessagingChannelAbandon()
        {
            IProtocolGatewayMessage msg = null;

            ProtocolGatewayMessageConverter messageConverter = MakeProtocolGatewayMessageConverter();
            var dp = new DeviceProxy(Channel.Object, Identity.Object, messageConverter);
            var cloudProxy = new Mock<ICloudProxy>();
            cloudProxy.Setup(d => d.SendFeedbackMessageAsync(It.IsAny<IFeedbackMessage>())).Callback<IFeedbackMessage>(
                m =>
                {
                    Assert.Equal(m.FeedbackStatus, FeedbackStatus.Abandon);
                });
            cloudProxy.Setup(d => d.BindCloudListener(It.IsAny<ICloudListener>()));
            var deviceListner = new DeviceListener(Identity.Object, EdgeHub.Object, ConnectionManager.Object, cloudProxy.Object);
            var messagingServiceClient = new Mqtt.MessagingServiceClient(deviceListner, messageConverter);

            Channel.Setup(r => r.Handle(It.IsAny<IProtocolGatewayMessage>()))
                .Callback<IProtocolGatewayMessage>(
                    m =>
                    {
                        msg = m;
                        messagingServiceClient.AbandonAsync(msg.Id);
                    });

            messagingServiceClient.BindMessagingChannel(Channel.Object);
            Core.IMessage message = new MqttMessage.Builder(new byte[] { 1, 2, 3 }).Build();
            await dp.SendMessageAsync(message);

            Assert.NotNull(msg);
        }

        [Fact]
        public async Task TestReceiveMessagingChannelDispose()
        {
            IProtocolGatewayMessage msg = null;

            ProtocolGatewayMessageConverter messageConverter = MakeProtocolGatewayMessageConverter();
            var dp = new DeviceProxy(Channel.Object, Identity.Object, messageConverter);
            var cloudProxy = new Mock<ICloudProxy>();
            cloudProxy.Setup(d => d.CloseAsync()).Callback(
                () =>
                {

                });
            cloudProxy.Setup(d => d.BindCloudListener(It.IsAny<ICloudListener>()));
            var deviceListner = new DeviceListener(Identity.Object, EdgeHub.Object, ConnectionManager.Object, cloudProxy.Object);
            var messagingServiceClient = new Mqtt.MessagingServiceClient(deviceListner, messageConverter);

            Channel.Setup(r => r.Handle(It.IsAny<IProtocolGatewayMessage>()))
                .Callback<IProtocolGatewayMessage>(
                    m =>
                    {
                        msg = m;
                        messagingServiceClient.DisposeAsync(new Exception("Some issue"));
                    });

            messagingServiceClient.BindMessagingChannel(Channel.Object);
            Core.IMessage message = new MqttMessage.Builder(new byte[] { 1, 2, 3 }).Build();
            await dp.SendMessageAsync(message);

            Assert.NotNull(msg);
        }
    }
}