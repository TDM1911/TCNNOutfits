using System.Collections.Generic;
using UnityEngine;

namespace TCNNOutfits.Core
{
    public interface IOutfitSource
    {
        string Name { get; }
        IEnumerable<OutfitDefinition> Load();
    }

}
