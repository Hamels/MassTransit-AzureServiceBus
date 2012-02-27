using System;
using MassTransit.Async;
using MassTransit.AzureServiceBus;
using MassTransit.Logging;
using MassTransit.Transports.AzureServiceBus.Util;
using Microsoft.ServiceBus.Messaging;

namespace MassTransit.Transports.AzureServiceBus
{
	/// <summary>
	/// Tiny wrapper around the F# receiver that is started only when the connection is
	/// bound and not before. The reason for this is that the receiver needs the management
	/// to do its work before becoming active. Possibly, the interface and hence implementation
	/// that calls the start and stop methods could be moved to the F# project.
	/// </summary>
	class PerConnectionReceiver
		: ConnectionBinding<ConnectionImpl>
	{
		readonly AzureServiceBusEndpointAddress _address;
		static readonly ILog _logger = Logger.Get(typeof (PerConnectionReceiver));

		readonly ReceiverSettings _settings;
		Receiver _receiver;

		public PerConnectionReceiver(
			[NotNull] AzureServiceBusEndpointAddress address, 
			[CanBeNull] ReceiverSettings settings)
		{
			if (address == null) throw new ArgumentNullException("address");
			
			_address = address;
			_settings = settings;
		}

		public void Bind(ConnectionImpl connection)
		{
			_logger.DebugFormat("starting receiver for '{0}'", _address);

			if (_receiver == null)
				_receiver = ReceiverModule.StartReceiver(_address, _settings);
		}

		public void Unbind(ConnectionImpl connection)
		{
			_logger.DebugFormat("stopping receiver for '{0}'", _address);

			if (_receiver == null)
				return;

			((IDisposable)_receiver).Dispose();
			_receiver = null;
		}

		public BrokeredMessage Get(TimeSpan timeout)
		{
			if (_receiver == null)
				throw new ApplicationException("Call Bind before calling Get");

			return _receiver.Get(timeout);
		}
	}
}