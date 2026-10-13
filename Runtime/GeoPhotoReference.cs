using System;
using System.Collections.Generic;

namespace GeoWorld
{
    [Serializable]
    public class GeoReferenceLicense
    {
        public string name, url;
    }

    [Serializable]
    public class GeoPhotoReference
    {
        public string source, photoId, observationId, sourceUrl, capturedAt, confidence, description, evidence, attribution;
        public GeoReferenceLicense license;
    }

    [Serializable]
    public class GeoBuildingVisualDetails : GeoPhotoReference
    {
        public string wallColor, roofColor, roofShape, wallMaterial;
        public string libraryId, windowStyle, style, roofNote;
        public int levels;
        public List<GeoPhotoReference> references = new List<GeoPhotoReference>();
    }
}
