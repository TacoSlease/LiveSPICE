﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SyMath;

namespace LiveSPICE
{
    /// <summary>
    /// Interaction logic for Simulation.xaml
    /// </summary>
    public partial class TransientSimulation : Window, INotifyPropertyChanged
    {
        protected int oversample = 4;
        public int Oversample
        {
            get { return oversample; }
            set { oversample = value; Build(); NotifyChanged("Oversample"); }
        }

        protected int iterations = 1;
        public int Iterations
        {
            get { return iterations; }
            set { iterations = value; NotifyChanged("Iterations"); }
        }

        protected Circuit.Quantity input = new Circuit.Quantity("V1[t]", Circuit.Units.V);
        public Circuit.Quantity Input
        {
            get { return input; }
            set { input = value; NotifyChanged("Input"); }
        }

        protected Circuit.Quantity output = new Circuit.Quantity("O1[t]", Circuit.Units.V);
        public Circuit.Quantity Output
        {
            get { return output; }
            set { output = value; NotifyChanged("Output"); }
        }

        protected Circuit.Quantity sampleRate = new Circuit.Quantity(48e3m, Circuit.Units.Hz);
        public Circuit.Quantity SampleRate
        {
            get { return sampleRate; }
            set { sampleRate.Set(value); NotifyChanged("SampleRate"); }
        }

        protected Circuit.Quantity latency = new Circuit.Quantity(50e-3m, Circuit.Units.s);
        public Circuit.Quantity Latency
        {
            get { return latency; }
            set { latency.Set(value); NotifyChanged("Latency"); }
        }

        protected int bitsPerSample = 16;
        public int BitsPerSample
        {
            get { return bitsPerSample; }
            set { bitsPerSample = value; NotifyChanged("BitsPerSample"); }
        }

        protected Circuit.Quantity inputGain = new Circuit.Quantity(1, Circuit.Units.None);
        public Circuit.Quantity InputGain
        {
            get { return inputGain; }
            set { inputGain.Set(value); NotifyChanged("InputGain"); }
        }

        protected Circuit.Quantity outputGain = new Circuit.Quantity(1, Circuit.Units.None);
        public Circuit.Quantity OutputGain
        {
            get { return outputGain; }
            set { outputGain.Set(value); NotifyChanged("OutputGain"); }
        }

        protected Circuit.Simulation simulation = null;
        protected AudioIo.WaveIo waveIo = null;

        protected Dictionary<SyMath.Expression, double[]> probes = new Dictionary<SyMath.Expression, double[]>();

        public TransientSimulation(Circuit.Schematic Simulate)
        {
            InitializeComponent();

            Closed += OnClosed;

            // Make a clone of the schematic so we can mess with it.
            Circuit.Schematic clone = Circuit.Schematic.Deserialize(Simulate.Serialize(), log);
            clone.Elements.ItemAdded += OnElementAdded;
            clone.Elements.ItemRemoved += (o, e) => RefreshProbes();

            schematic.Schematic = new SimulationSchematic(clone);
            
            waveIo = new AudioIo.WaveIo(ProcessSamples, (int)sampleRate, 1, bitsPerSample, (double)latency);

            Build();
        }

        private void OnElementAdded(object sender, Circuit.ElementEventArgs e)
        {
            if (e.Element is Circuit.Symbol && ((Circuit.Symbol)e.Element).Component is Probe)
                e.Element.LayoutChanged += (x, y) => RefreshProbes();
            RefreshProbes();
        }

        private void Probe_Click(object sender, RoutedEventArgs e)
        {
            schematic.Schematic.Tool = new ProbeTool((SimulationSchematic)schematic.Schematic);
            schematic.Schematic.Focus();
        }

        public void RefreshProbes()
        {
            lock (probes)
            {
                probes.Clear();
                foreach (Probe i in ((SimulationSchematic)schematic.Schematic).Probes.Where(i => i.ConnectedTo != null))
                {
                    probes[i.V] = new double[0];

                    // If this signal isn't already in the oscilloscope, add it.
                    if (!oscilloscope.Signals.Contains(i.V))
                    {
                        Pen p;
                        switch (i.Color)
                        {
                            // These two need to be brighter than the normal colors.
                            case Circuit.EdgeType.Red: p = new Pen(new SolidColorBrush(Color.FromRgb(255, 50, 50)), 1.0); break;
                            case Circuit.EdgeType.Blue: p = new Pen(new SolidColorBrush(Color.FromRgb(20, 180, 255)), 1.0); break;
                            default: p = ElementControl.MapToPen(i.Color); break;
                        }
                        oscilloscope.AddSignal(i.V, p);
                    }
                }

                // Remove signals that aren't being processed from the oscilloscope.
                foreach (SyMath.Expression i in oscilloscope.Signals.ToArray())
                    if (!probes.ContainsKey(i))
                        oscilloscope.RemoveSignal(i);
            }
        }

        private BackgroundWorker builder;
        protected void Build()
        {
            simulation = null;
            try
            {
                Circuit.Circuit circuit = schematic.Schematic.Schematic.Build(log);
                builder = new BackgroundWorker();
                builder.DoWork += (o, e) =>
                {
                    try
                    {
                        Circuit.TransientSolution TS = Circuit.TransientSolution.SolveCircuit(circuit, 1 / (sampleRate * Oversample), log);
                        simulation = new Circuit.LinqCompiledSimulation(TS, Oversample, log);
                    }
                    catch (System.Exception ex)
                    {
                        log.WriteLine(Circuit.MessageType.Error, ex.Message);
                    }
                };
                builder.RunWorkerAsync();
            }
            catch (System.Exception ex)
            {
                log.WriteLine(Circuit.MessageType.Error, ex.Message);
            }
        }
        
        private void ProcessSamples(double[] Samples, int Rate)
        {
            // If there is no simulation, just zero the samples and return.
            if (simulation == null)
            {
                for (int i = 0; i < Samples.Length; ++i)
                    Samples[i] = 0.0;
                return;
            }

            try
            {
                // Apply input gain.
                double inputGain = (double)InputGain;
                if (System.Math.Abs(inputGain - 1.0) > 1e-2)
                    for (int i = 0; i < Samples.Length; ++i)
                        Samples[i] *= inputGain;

                lock (probes)
                {
                    // Build the signal list.
                    foreach (SyMath.Expression i in probes.Keys.ToArray())
                        if (probes[i].Length < Samples.Length)
                            probes[i] = new double[Samples.Length];

                    IEnumerable<KeyValuePair<SyMath.Expression, double[]>> output = probes.Append(new KeyValuePair<SyMath.Expression, double[]>(Output.Value, Samples));

                    // Process the samples!
                    simulation.Run(Input, Samples, output, Iterations);

                    // Show the samples on the oscilloscope.
                    oscilloscope.ProcessSignals(Samples.Length, output, new Circuit.Quantity(Rate, Circuit.Units.Hz));
                }

                // Apply output gain.
                double outputGain = (double)OutputGain;
                if (System.Math.Abs(outputGain - 1.0) > 1e-2)
                    for (int i = 0; i < Samples.Length; ++i)
                        Samples[i] *= outputGain;
            }
            //catch (OverflowException ex)
            //{
            //    // If the simulation diverged, reset it and hope it doesn't happen again.
            //    log.WriteLine(Circuit.MessageType.Error, ex.Message);
            //    simulation.Reset();
            //}
            catch (Exception ex)
            {
                // If there was a more serious error, kill the simulation so the user can fix it.
                log.WriteLine(Circuit.MessageType.Error, ex.Message);
                simulation = null;
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            waveIo.Dispose();
            waveIo = null;
        }

        private void Simulate_Executed(object sender, ExecutedRoutedEventArgs e) { Build(); }

        private void Exit_Executed(object sender, ExecutedRoutedEventArgs e) { Close(); }

        // INotifyPropertyChanged.
        private void NotifyChanged(string p)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(p));
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }
}