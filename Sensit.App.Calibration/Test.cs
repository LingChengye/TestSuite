﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Sensit.TestSDK.Exceptions;
using Sensit.TestSDK.Interfaces;

namespace Sensit.App.Calibration
{
	public class Test
	{
		#region Enumerations

		/// <summary>
		/// Non-measurement related commands that can be executed during a test.
		/// </summary>
		/// <remarks>
		/// These actions can activate a relay, send info to the DUTs, etc.
		/// </remarks>
		public enum Command
		{
			TurnOff,	// Remove power from DUT.
			TurnOn,		// Apply power to DUT.			  
			Default,	// Set factory default settings.
			Range,		// Set range settings.
			Zero,		// Perform zero-calibration.
			Span,		// Perform span-calibration.
		}

		/// <summary>
		/// Types of tolerances for DUT ranges
		/// </summary>
		public enum ToleranceType
		{
			Absolute,			// Quantity of range.
			PercentFullScale,   // Percent of positive range.
			PercentReading      // Percent of reading.
		}

		#endregion

		#region Fields

		private BackgroundWorker _testThread;   // task that will handle test operations
		private TestSetting _settings;			// settings for test
		private Equipment _equipment;			// test equipment object
		private readonly List<Dut> _duts;       // devices under test
		private Stopwatch _elapsedTimeStopwatch;// keeper of test's elapsed time
		private bool _pause = false;			// whether test is paused
		private int _samplesTotal;				// helps calculate percent complete
		private int _samplesComplete = 0;       // helps calculate percent complete
		private double? _previous = null;		// used to calculate rate of change in StabilityCheck

		#endregion

		#region Delegates

		// Report test progress.
		public Action<int, string> UpdateProgress;

		// Report the value of the independent variable.
		public Action<double> UpdateIndependentVariable;

		// Report the rate of change of the independent variable.
		public Action<double> UpdateRateOfChange;

		// Report the independent variable's maximum and minimum allowed value.
		public Action<(double min, double max)> UpdateIndependentVariableRange;

		// Report the independent variable's maximum and minimum allowed rate of change.
		public Action<(double min, double max)> UpdateRateRange;

		// Report the controlled variables and their values.
		public Action<Dictionary<VariableType, double>> UpdateVariables;

		// Report test results.
		public Action Finished;

		#endregion

		#region Properties

		/// <summary>
		/// Progress of the test, in percent.
		/// </summary>
		public int PercentProgress
		{
			get
			{
				int percent = (int)(_samplesComplete / (double)_samplesTotal * 100.0);

				// Check for overflow or underflow.
				if ((percent < 0) || (percent > 100))
				{
					throw new TestException("Percent progress is out of range; please contact Engineering."
						+ Environment.NewLine + "Steps Complete:  " + _samplesComplete.ToString()
						+ Environment.NewLine + "Steps Total:  " + _samplesTotal.ToString());
				}

				return percent;
			}
		}

		public float ErrorValue { get; private set; }

		/// <summary>
		/// Controlled/independent variables for the current test component.
		/// </summary>
		/// <remarks>
		/// This will be accessed by the FormCalibration to display the variables values.
		/// </remarks>
		public List<TestControlledVariable> Variables;

		#endregion

		#region Thread Management

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="equipment">equipment used by the test</param>
		/// <param name="duts">devices being tested</param>
		public Test(TestSetting settings, Equipment equipment, List<Dut> duts)
		{
			// Save the reference to the equipment and DUTs objects.
			_settings = settings;
			_equipment = equipment;
			_duts = duts;

			// Set up the background worker.
			_testThread = new BackgroundWorker
			{
				WorkerReportsProgress = true,
				WorkerSupportsCancellation = true
			};
			_testThread.DoWork += TestThread;
			_testThread.ProgressChanged += ProgressChanged;
			_testThread.RunWorkerCompleted += RunWorkerCompleted;

			// Calculate how many samples we'll take in the selected test.
			// This allows us to calculate the test's percent progress.
			_samplesTotal = 0;
			foreach (TestComponent c in settings.Components)
			{
				foreach (TestControlledVariable v in c?.ControlledVariables ?? Enumerable.Empty<TestControlledVariable>())
				{
					foreach (double sp in v?.Setpoints ?? Enumerable.Empty<double>())
					{
						_samplesTotal += v.Samples;
					}
				}
			}
		}

		/// <summary>
		/// Start a new test.
		/// </summary>
		public void Start()
		{
			// Run "TestProcess" asynchronously (using a background worker).
			if (_testThread.IsBusy == false)
			{
				// Start the asynchronous operation.
				_testThread.RunWorkerAsync();
			}
		}

		/// <summary>
		/// Stop the test.
		/// </summary>
		public void Stop()
		{
			// Cancel the test operation.
			_testThread.CancelAsync();
		}

		/// <summary>
		/// Pause the test.
		/// </summary>
		public void Pause()
		{
			// Set a global flag which will cause the test to pause at a convenient spot.
			_pause = true;
		}

		/// <summary>
		/// Return whether a test is running.
		/// </summary>
		/// <returns>true if test is running; false otherwise</returns>
		public bool IsBusy()
		{
			return _testThread.IsBusy;
		}

		private void ProgressChanged(object sender, ProgressChangedEventArgs e)
		{
			// Run action required as test progresses (i.e. update GUI).
			UpdateProgress(e.ProgressPercentage, e.UserState as string);
		}

		private void RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
		{
			// If the test was cancelled, update the GUI's status accordingly.
			if (e.Cancelled)
			{
				UpdateProgress(0, "Test cancelled.");
			}

			// Run actions required when test is completed (i.e. update GUI).
			Finished?.Invoke();
		}

		#endregion

		/// <summary>
		/// If we can't reach a setpoint, stop the equipment and prompt the user.
		/// </summary>
		/// <remarks>
		/// Measure/vent mode must be supported for all devices.
		/// Measure mode should save active setpoints but not use them.
		/// Equipment has properties for control devices and reference devices.
		/// Those properties are Dictionaries with the key being the variable type.
		/// Then we can initialize them in Equipment.cs, iterate through them here, and set parameters when needed.
		/// </remarks>
		/// <param name="message">message to display to the user</param>
		/// <param name="caption">caption for the message</param>
		private void PopupRetryAbort(string message, string caption)
		{
			// Stop the equipment to reduce change of damage.
			foreach (KeyValuePair<VariableType, IControlDevice> c in _equipment.Controllers)
			{
				c.Value.SetControlMode(ControlMode.Measure);
			}

			// Pause the elapsed time timer.
			_elapsedTimeStopwatch.Stop();

			// Alert the user (asking if they wish to retry or not).
			DialogResult result = MessageBox.Show(message, caption, MessageBoxButtons.YesNo);

			// Resume timing elapsed time.
			_elapsedTimeStopwatch.Start();

			// If we're continuing to test, attempt to control variables again.
			if (result == DialogResult.Yes)
			{
				foreach (KeyValuePair<VariableType, IControlDevice> c in _equipment.Controllers)
				{
					// Resume control mode.
					c.Value.SetControlMode(ControlMode.Control);
				}
			}
			// If requested, cancel the test.
			else
			{
				// TODO:  Log the abort action.

				// Abort the test.
				_testThread.CancelAsync();
			}
		}

		/// <summary>
		/// If some uncorrectable error occurs, stop the equipment and alert the user.
		/// </summary>
		/// <remarks>
		/// Because this is called when an uncorrectable error occurs, we can't allow
		/// this method to be interrupted by exceptions.  So we log them, but allow the
		/// method to continue.
		/// </remarks>
		/// <param name="errorMessage">message to display to the user</param>
		/// <param name="caption">caption for the message</param>
		private void PopupAbort(string errorMessage, string caption)
		{
			// Stop the equipment to reduce change of damage.
			foreach (KeyValuePair<VariableType, IControlDevice> c in _equipment.Controllers)
			{
				try
				{
					c.Value.SetControlMode(ControlMode.Measure);
				}
				catch (DeviceException)
				{
					// TODO:  Log failures.
				}
			}

			// Alert the user.
			MessageBox.Show(errorMessage, caption);
		}

		/// <summary>
		/// Check whether all controlled variables are within stability tolerances.
		/// </summary>
		private void StabilityCheck(List<TestControlledVariable> controlledVariables)
		{
			// For each controller...
			foreach (TestControlledVariable v in controlledVariables)
			{
				// Read the setpoint.
				double setpoint = _equipment.Controllers[v.VariableType].ReadSetpoint(v.VariableType);

				// Read the reference reading.
				double reading = _equipment.References[v.VariableType].Readings[v.VariableType];

				// TODO:  Update the error and rate of change for the independent variable only.
				if (v.VariableType == VariableType.GasConcentration)
				{
					// Update the error in the independent variable.
					UpdateIndependentVariable?.Invoke(reading);

					// If we know the previous reading...
					if (_previous != null)
					{
						// Calculate rate of change.
						double rate = (reading - (double)_previous) / v.Interval.TotalSeconds;

						// Update the rate of change of the independent variable.
						UpdateRateOfChange?.Invoke(rate);
					}

					// Save the previous reading.
					_previous = reading;
				}

				// If the reading is out of tolerance...
				if (Math.Abs(setpoint - reading) > v.ErrorTolerance)
				{
					// Attempt to achieve the setpoint again.
					ProcessSetpoint(v, setpoint, v.Interval);
				}
			}
		}

		/// <summary>
		/// Check if the test should be paused.
		/// </summary>
		/// <remarks>
		/// This should be called at convenient intervals within the test thread
		/// where the test can be paused.
		/// </remarks>
		private void ProcessPause()
	{
			// All we need to do is call the error method with a message for the user.
			PopupRetryAbort("The test is paused.  Continue?", "Notice");

			// Reset the flag once the user resumes the test.
			_pause = false;
		}

		private void ProcessCommand(Command? command)
		{
			switch (command)
			{
				case Command.TurnOff:
				case Command.TurnOn:
				case Command.Default:
				case Command.Range:
				case Command.Span:
				case Command.Zero:
				default:
					break;
			}
		}

		private void ProcessSetpoint(TestControlledVariable variable, double setpoint, TimeSpan interval)
		{
			// Update GUI.
			_testThread.ReportProgress(PercentProgress, "Setting setpoint...");

			// Set setpoint.
			_equipment.Controllers[variable.VariableType].WriteSetpoint(variable.VariableType, setpoint);

			// Get start time.
			Stopwatch stopwatch = Stopwatch.StartNew();
			Stopwatch timeoutWatch = Stopwatch.StartNew();

			double previous = setpoint;

			// Take readings until they are within tolerance for the required settling time.
			double error;
			double rate;
			do
			{
				// Abort if requested.
				if (_testThread.CancellationPending) { break; }

				// Process timeouts.
				if (timeoutWatch.Elapsed > variable.Timeout)
				{
					// Prompt user; cancel test if requested.
					PopupRetryAbort("Not able to reach stability." + Environment.NewLine + "Retry ?", "Stability Error");

					// Reset the timeout stopwatch.
					timeoutWatch.Restart();
				}

				// Process pause requests.
				if (_pause == true)
				{
					// Stop the clocks.
					stopwatch.Stop();
					timeoutWatch.Stop();

					// Pause the test.
					ProcessPause();

					// Restart the clocks.
					stopwatch.Start();
					timeoutWatch.Start();
				}

				// Get reference reading.
				_equipment.References[variable.VariableType].Read();
				double reading = _equipment.References[variable.VariableType].Readings[variable.VariableType];

				// Calculate error.
				error = reading - setpoint;

				// Update the error in the independent variable.
				UpdateIndependentVariable?.Invoke(reading);

				// Calculate rate of change.
				rate = (reading - previous) / (interval.TotalSeconds);
				previous = reading;

				// Update the rate of change of the independent variable.
				UpdateRateOfChange?.Invoke(rate);

				// If tolerance has been exceeded, reset the stability time.
				if (Math.Abs(error) > variable.ErrorTolerance)
				{
					stopwatch.Restart();
				}

				// Update GUI (include dwell time if applicable).
				string message = "Waiting for stability...";
				if (variable.DwellTime > new TimeSpan(0, 0, 0) &&
					(Math.Abs(error) < variable.ErrorTolerance) &&
					(Math.Abs(rate) < variable.RateTolerance))
				{
					message += "dwell time left:  " + (variable.DwellTime - stopwatch.Elapsed).ToString(@"hh\:mm\:ss");

					// Reset the timeout stopwatch.
					timeoutWatch.Restart();
				}
				_testThread.ReportProgress(PercentProgress, message);

				// Wait to get desired reading frequency.
				Thread.Sleep(interval);
			} while ((stopwatch.Elapsed <= variable.DwellTime) ||
					(Math.Abs(error) > variable.ErrorTolerance) ||
					(Math.Abs(rate) > variable.RateTolerance));
		}

		private void ProcessSamples(double setpoint)
		{
			// Create an object to hold reference device readings.
			Dictionary<VariableType, double> referenceReadings = new Dictionary<VariableType, double>();

			// Add reference data to the dictionary.
			foreach (VariableType reference in _settings?.References ?? Enumerable.Empty<VariableType>())
			{
				double value = _equipment.References[reference].Readings[reference];

				referenceReadings.Add(reference, value);
			}

			// Record the data applicable to each DUT.
			foreach (Dut dut in _duts)
			{
				// TODO:  The DUT doesn't really care about the reference reading.
				// Take it out and log it elsewhere (maybe a few lines above?).
				dut.Read(_elapsedTimeStopwatch.Elapsed, setpoint, referenceReadings[VariableType.GasConcentration]);

				if (_testThread.CancellationPending) { break; }
			}
		}

		/// <summary>
		/// Perform all requested test actions in order.
		/// </summary>
		/// <remarks>
		/// This method is basically a bunch of nested foreach loops corresponding to the test settings object.
		/// Note that for cancelling a test to work correctly, each foreach loop must contain this line, or
		/// else it will hang in that loop when a test is aborted:
		/// <code>
		/// if (_testThread.CancellationPending) { break; }
		/// </code>
		/// </remarks>
		private void ProcessTest()
		{
			// For each component...
			foreach (TestComponent c in _settings?.Components ?? Enumerable.Empty<TestComponent>())
			{
				// For each DUT command...
				foreach (Command command in c?.Commands ?? Enumerable.Empty<Command>())
				{
					// Perform DUT command.
					ProcessCommand(command);

					if (_testThread.CancellationPending) { break; }
				}

				// For each controlled variable...
				foreach (TestControlledVariable v in c?.ControlledVariables ?? Enumerable.Empty<TestControlledVariable>())
				{
					// Set active control mode.
					_equipment.Controllers[v.VariableType].SetControlMode(ControlMode.Control);

					// For each setpoint...
					foreach (double sp in v?.Setpoints ?? Enumerable.Empty<double>())
					{
						// Update the form.
						UpdateIndependentVariableRange((sp - v.ErrorTolerance, sp + v.ErrorTolerance));
						UpdateRateRange((v.RateTolerance * -1.0, v.RateTolerance));

						// Set the setpoint.
						ProcessSetpoint(v, sp, v.Interval);

						// For each sample...
						for (int i = 1; i <= v.Samples; i++)
						{
							// Update GUI.
							_testThread.ReportProgress(PercentProgress, "Taking sample " + i.ToString() + " of " + v.Samples + ".");

							// Fetch readings from references and DUTs.
							_equipment.Read();

							// Check stability of all controlled variables.
							StabilityCheck(c.ControlledVariables);

							// Pause the test if necessary.
							if (_pause == true)
							{
								ProcessPause();
							}

							// Record sample data.
							ProcessSamples(sp);

							// Wait to get desired reading frequency.
							Thread.Sleep(v.Interval);

							_samplesComplete++;

							if (_testThread.CancellationPending) { break; }
						}

						if (_testThread.CancellationPending) { break; }
					}

					if (_testThread.CancellationPending) { break; }
				}

				if (_testThread.CancellationPending) { break; }
			}
		}

		/// <summary>
		/// This runs during a test.
		/// </summary>
		/// <remarks>
		/// This method handles all the testing of the DUTs.  Every time the
		/// user presses "Start" this is what runs.  If you're trying to figure
		/// out what this application does, this is a good place to start.
		/// 
		/// This page helped guide implementation of how to cancel the task:
		/// https://docs.microsoft.com/en-us/dotnet/framework/winforms/controls/how-to-run-an-operation-in-the-background
		/// </remarks>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void TestThread(object sender, DoWorkEventArgs e)
		{
			// Get start time.
			_elapsedTimeStopwatch = Stopwatch.StartNew();

			try
			{
				// Anything within this do-while structure can be cancelled.
				do
				{
					// Initialize test equipment.
					_testThread.ReportProgress(0, "Configuring test equipment...");
					bool useDatalogger = false;
					foreach (Dut dut in _duts)
					{
						if (_testThread.CancellationPending) { break; }

						dut.Open();

						// Figure out if we need to configure the datalogger.
						if (dut.Type.Equals("Datalogger"))
						{
							useDatalogger = true;
						}
					}
					_equipment.Open(useDatalogger);

					// Initialize DUT interface device if necessary.
					if (useDatalogger)
					{
						_equipment.DutInterface.Configure();
					}

					// Perform test actions.
					ProcessTest();
				} while (false);
			}
			catch (Exception ex)
			{
				PopupAbort(ex.Message, ex.GetType().ToString());
			}

			// Everything between here and the end of the test should be fast
			// and highly reliable since it cannot be cancelled.

			// Calculate end time.
			_elapsedTimeStopwatch.Stop();
			TimeSpan elapsedtime = _elapsedTimeStopwatch.Elapsed;

			try
			{
				// Stop all controllers.
				foreach (KeyValuePair<VariableType, IControlDevice> c in _equipment.Controllers)
				{
					c.Value.SetControlMode(ControlMode.Ambient);
				}

				// Close DUTs; save CSV files.
				foreach (Dut dut in _duts)
				{
					dut.Close();
				}

				// Close test equipment.
				_testThread.ReportProgress(99, "Closing test equipment...");
				_equipment.Close();
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, ex.GetType().ToString());
			}

			// Update the GUI.
			_testThread.ReportProgress(100, "Done.");
			MessageBox.Show("Test complete.", "Notice");

			// If the operation was cancelled by the user, set the cancel property.
			if (_testThread.CancellationPending) { e.Cancel = true; }
		}
	}
}
