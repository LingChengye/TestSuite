﻿namespace Sensit.TestSDK.Interfaces
{
	/// <summary>
	/// What the device should try to do.
	/// </summary>
	public enum ControlMode
	{
		Ambient,	// vent to external environment if possible
		Control,	// actively controlling the test environment
		Measure		// passively measuring the test environment
	}

	/// <summary>
	/// Device that controls one or more independent variables in a test.
	/// </summary>
	/// <remarks>
	/// Don't implement this interface directly.
	/// Devices should implement one of the more specific interfaces below.
	/// </remarks>
	public interface IControlDevice
	{
		/// <summary>
		/// Change the device's control mode.
		/// </summary>
		/// <param name="mode"></param>
		void SetControlMode(ControlMode mode);
	}

	/// <summary>
	/// Device that controls gas concentration.
	/// </summary>
	public interface IGasMixController : IControlDevice
	{
		/// <summary>
		/// Concentration of the gas under test before dilution.
		/// </summary>
		double AnalyteBottleConcentration { get; set; }

		/// <summary>
		/// Value the device should try to achieve when in Control mode.
		/// </summary>
		double AnalyteConcentrationSetpoint { get; set; }

		/// <summary>
		/// Value the device should try to achieve when in Control Mode.
		/// </summary>
		double MassFlowSetpoint { get; set; }

		// void WriteAnalyteConcentrationSetpoint();
		// void WriteAnalyteConcentrationSetpoint(double setpoint);
		// double ReadAnalyteConcentationSetpoint();
		void WriteSetpoint();
		void WriteSetpoint(double concentration, double massFlow);
		double ReadSetpoint();
	}

	/// <summary>
	/// Device that controls gas mass flow.
	/// </summary>
	public interface IMassFlowController : IControlDevice
	{
		/// <summary>
		/// Value the device should try to achieve when in Control Mode.
		/// </summary>
		double MassFlowSetpoint { get; set; }

		/// <summary>
		/// Write the setpoint to the device.
		/// </summary>
		/// <remarks>
		/// Some processes need to control multiple variables at a time,
		/// but only one variable needs to vary.
		/// So this interface supports sending each setpoint individually.
		/// </remarks>
		void WriteMassFlowSetpoint();

		/// <summary>
		/// Write the setpoint to the device.
		/// </summary>
		/// <remarks>
		/// For convenience, this method sets the property and writes it to the device.
		/// So we can write one line of code instead of two.
		/// </remarks>
		/// <param name="setpoint"></param>
		void WriteMassFlowSetpoint(double setpoint);

		/// <summary>
		/// Read the setpoint from the device.
		/// </summary>
		/// <remarks>
		/// For convenience, this method updates the property and returns the value.
		/// </remarks>
		double ReadMassFlowSetpoint();
	}

	/// <summary>
	/// Device that controls gas volume flow.
	/// </summary>
	public interface IVolumeFlowController : IControlDevice
	{
		double VolumeFlowSetpoint { get; set; }

		void WriteVolumeFlowSetpoint();

		void WriteVolumeFlowSetpoint(double setpoint);

		double ReadVolumeFlowSetpoint();
	}

	/// <summary>
	/// Device that controls gas velocity (i.e. a wind tunnel).
	/// </summary>
	public interface IVelocityController : IControlDevice
	{
		double VelocitySetpoint { get; set; }

		void WriteVelocitySetpoint();

		void WriteVelocitySetpoint(double setpoint);

		double ReadVelocitySetpoint();
	}

	/// <summary>
	/// Device that controls pressure.
	/// </summary>
	public interface IPressureController : IControlDevice
	{
		double PressureSetpoint { get; set; }

		void WritePressureSetpoint();

		void WritePressureSetpoint(double setpoint);

		double ReadPressureSetpoint();
	}

	/// <summary>
	/// Device that controls temperature.
	/// </summary>
	public interface ITemperatureController : IControlDevice
	{
		double TemperatureSetpoint { get; set; }

		void WriteTemperatureSetpoint();

		void WriteTemperatureSetpoint(double setpoint);

		double ReadTemperatureSetpoint();
	}
}
