using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // JSON uses nullable numbers and a tag dictionary. Unity needs serialized
    // backing fields so the evidence survives a saved scene or an edited clone.
    [Serializable]
    public class GeoMappedAppearance : ISerializationCallbackReceiver
    {
        [Serializable] struct OptionalNumber
        {
            public bool present;
            public float value;
            public float? Get() => present ? (float?)value : null;
            public void Set(float? number) { present=number.HasValue;value=number.GetValueOrDefault(); }
        }
        [Serializable] struct Tag { public string key, value; }

        public string source, sourceUrl, attribution;
        public GeoReferenceLicense license;
        public string roofShape, roofColor, roofColorRaw, roofMaterial;
        public string roofDirectionMeaning, roofOrientation;
        public string wallMaterial, wallColor, wallColorRaw, buildingUse, architecture, startDate;
        public Dictionary<string,string> tags { get; set; } = new Dictionary<string,string>();
        [SerializeField] List<Tag> serializedTags = new List<Tag>();
        [SerializeField] OptionalNumber mappedRoofHeight, mappedRoofLevels, mappedRoofAngle, mappedRoofDirection, mappedMinHeight, mappedMinLevel;
        public float? roofHeight { get=>mappedRoofHeight.Get(); set=>mappedRoofHeight.Set(value); }
        public float? roofLevels { get=>mappedRoofLevels.Get(); set=>mappedRoofLevels.Set(value); }
        public float? roofAngle { get=>mappedRoofAngle.Get(); set=>mappedRoofAngle.Set(value); }
        public float? roofDirection { get=>mappedRoofDirection.Get(); set=>mappedRoofDirection.Set(value); }
        public float? minHeight { get=>mappedMinHeight.Get(); set=>mappedMinHeight.Set(value); }
        public float? minLevel { get=>mappedMinLevel.Get(); set=>mappedMinLevel.Set(value); }

        public void OnBeforeSerialize()
        {
            if(serializedTags==null)serializedTags=new List<Tag>();
            serializedTags.Clear();
            if(tags!=null)foreach(var tag in tags)serializedTags.Add(new Tag{key=tag.Key,value=tag.Value});
        }
        public void OnAfterDeserialize()
        {
            tags=new Dictionary<string,string>();
            if(serializedTags!=null)foreach(var tag in serializedTags)if(tag.key!=null)tags[tag.key]=tag.value;
        }
    }
}
