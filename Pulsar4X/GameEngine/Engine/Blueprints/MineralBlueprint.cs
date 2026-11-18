using System.Collections.Generic;
using Pulsar4X.DataStructures;

namespace Pulsar4X.Blueprints
{
    public class MineralBlueprint : Blueprint
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string CargoTypeID { get; set; }
        public long MassPerUnit { get; set; }
        public double VolumePerUnit { get; set; }
        public Dictionary<BodyType, double> Abundance { get; set; }

        /// <summary>
        /// Optional. A mineral burned straight as fuel carries the fuel formulas here
        /// (ExhaustVelocity, ChemicalEnergy, FuelType), as a processed fuel used to.
        /// </summary>
        public Dictionary<string, string> Formulas { get; set; }

        /// <summary>
        /// Extraction energy, kWh per kg, for its embodied energy. Optional: null is
        /// EmbodiedEnergy.DefaultExtraction_kWhPerKg (0.05, the mine's draw per kg).
        /// </summary>
        public double? ExtractionEnergy { get; set; }
        
        public string PartMatUniqueID { get; set; }
    }
}