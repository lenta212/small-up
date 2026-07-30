// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.
using Content.Shared._NF.Shuttles.Events;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Physics.Components;
using System.Numerics;
using Content.Shared._Mono.Company;
using Robust.Client.Graphics;
using Robust.Shared.Collections;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Client.Shuttles.UI
{
    public partial class ShuttleNavControl // Mono
    {
        public InertiaDampeningMode DampeningMode { get; set; }
        public bool HideTarget { get; set; } = true;
        public Vector2? Target { get; set; }
        public NetEntity? TargetEntity { get; set; }

        /// <summary>
        /// Whether the shuttle is currently in FTL. This is used to disable the Park button
        /// while in FTL to prevent parking while traveling.
        /// </summary>
        public bool InFtl { get; set; }

        private void NfUpdateState(NavInterfaceState state)
        {
            Target = state.Target;
            TargetEntity = state.TargetEntity;
            HideTarget = state.HideTarget;

            if (!EntManager.GetCoordinates(state.Coordinates).HasValue ||
                !EntManager.TryGetComponent(EntManager.GetCoordinates(state.Coordinates).GetValueOrDefault().EntityId,out TransformComponent? transform) ||
                !EntManager.TryGetComponent(transform.GridUid, out PhysicsComponent? physicsComponent))
            {
                return;
            }

            DampeningMode = state.DampeningMode;

            // Check if the entity has an FTLComponent which indicates it's in FTL
            if (transform.GridUid != null)
            {
                InFtl = EntManager.HasComponent<FTLComponent>(transform.GridUid);
            }
            else
            {
                InFtl = false;
            }
        }

        private void NfDrawRadarTarget(
            DrawingHandleScreen handle,
            Matrix3x2 worldToView,
            MapCoordinates radarPosition)
        {
            if (HideTarget || Target is not { } targetPosition)
                return;

            EntityUid? localTarget = null;
            if (TargetEntity is { } netTarget &&
                EntManager.TryGetEntity(netTarget, out var resolvedTarget) &&
                resolvedTarget is { } targetUid &&
                EntManager.TryGetComponent<TransformComponent>(targetUid, out var targetXform) &&
                targetXform.MapID == radarPosition.MapId)
            {
                localTarget = targetUid;
                targetPosition = _transform.GetMapCoordinates(targetUid, targetXform).Position;
            }

            var targetName = Loc.GetString("shuttle-console-target-name");
            if (localTarget is { } target &&
                EntManager.TryGetComponent<MetaDataComponent>(target, out var metadata))
            {
                targetName = metadata.EntityName;
            }

            var labelColor = Color.FromHex("#7BE6C5");
            var coordColor = labelColor.WithAlpha(0.6f);
            var uiPosition = Vector2.Transform(targetPosition, worldToView) / UIScale;
            var uiCenter = new Vector2(Width * 0.5f, Height * 0.5f);
            var offset = uiPosition - uiCenter;
            var distanceFromCenter = offset.Length();
            var radarRadius = MathF.Max(1f, MathF.Min(uiCenter.X, uiCenter.Y) * 0.95f);
            var isOutsideRadarCircle = distanceFromCenter > radarRadius;

            if (isOutsideRadarCircle && distanceFromCenter > float.Epsilon)
                uiPosition = uiCenter + offset / distanceFromCenter * radarRadius;

            var distance = Vector2.Distance(targetPosition, radarPosition.Position);
            var displayedDistance = distance < 50f
                ? $"{distance:0.0}"
                : distance < 1000f
                    ? $"{distance:0}"
                    : $"{distance / 1000f:0.0}k";
            var labelText = Loc.GetString(
                "shuttle-console-iff-label",
                ("name", targetName),
                ("distance", displayedDistance));
            var coordsText = $"({targetPosition.X:0.0}, {targetPosition.Y:0.0})";
            var labelDimensions = handle.GetDimensions(Font, labelText, 0.9f);
            var blipSize = RadarBlipSize * 0.7f;
            var labelOnLeft = uiPosition.X >= uiCenter.X;
            var labelOffset = new Vector2(
                labelOnLeft ? -labelDimensions.X - blipSize : blipSize,
                -labelDimensions.Y * 0.5f);

            handle.DrawString(
                Font,
                (uiPosition + labelOffset) * UIScale,
                labelText,
                UIScale * 0.9f,
                labelColor);

            var mousePosition = _uiManager.MousePositionScaled.Position - GlobalPosition;
            if (!HideCoords && Vector2.Distance(mousePosition, uiPosition) < 30f)
            {
                var coordDimensions = handle.GetDimensions(Font, coordsText, 0.7f);
                var coordOffset = new Vector2(
                    labelOnLeft ? -coordDimensions.X - blipSize : blipSize,
                    labelDimensions.Y * 0.5f);
                handle.DrawString(
                    Font,
                    (uiPosition + coordOffset) * UIScale,
                    coordsText,
                    UIScale * 0.7f,
                    coordColor);
            }

            NfAddBlipToList(
                _tempBlipDataList,
                isOutsideRadarCircle,
                uiPosition,
                (int) uiCenter.X,
                (int) uiCenter.Y,
                labelColor);
        }

        // New Frontiers - Maximum IFF Distance - checks distance to object, draws if closer than max range
        // This code is licensed under AGPLv3. See AGPLv3.txt
        private bool NfCheckShouldDrawIffRangeCondition(bool shouldDrawIff, Vector2 distance)
        {
            if (shouldDrawIff && MaximumIFFDistance >= 0.0f)
            {
                if (distance.Length() > MaximumIFFDistance)
                {
                    shouldDrawIff = false;
                }
            }

            return shouldDrawIff;
        }

        private static void NfAddBlipToList(List<BlipData> blipDataList, bool isOutsideRadarCircle, Vector2 uiPosition, int uiXCentre, int uiYCentre, Color color)
        {
            blipDataList.Add(new BlipData
            {
                IsOutsideRadarCircle = isOutsideRadarCircle,
                UiPosition = uiPosition,
                VectorToPosition = uiPosition - new Vector2(uiXCentre, uiYCentre),
                Color = color
            });
        }

        private static void NfAddBlipToList(List<BlipData> blipDataList, bool isOutsideRadarCircle, Vector2 uiPosition, int uiXCentre, int uiYCentre, Color color, EntityUid gridUid = default)
        {
            // Check if the entity has a company component and use that color if available
            Color blipColor = color;

            if (gridUid != default &&
                IoCManager.Resolve<IEntityManager>().TryGetComponent(gridUid, out Shared._Mono.Company.CompanyComponent? companyComp) &&
                !string.IsNullOrEmpty(companyComp.CompanyName))
            {
                var prototypeManager = IoCManager.Resolve<IPrototypeManager>();
                if (prototypeManager.TryIndex<CompanyPrototype>(companyComp.CompanyName, out var prototype) && prototype != null)
                {
                    blipColor = prototype.Color;
                }
            }

            blipDataList.Add(new BlipData
            {
                IsOutsideRadarCircle = isOutsideRadarCircle,
                UiPosition = uiPosition,
                VectorToPosition = uiPosition - new Vector2(uiXCentre, uiYCentre),
                Color = blipColor
            });
        }

        /**
         * Frontier - Adds blip style triangles that are on ships or pointing towards ships on the edges of the radar.
         * Draws blips at the BlipData's uiPosition and uses VectorToPosition to rotate to point towards ships.
         */
        private void NfDrawBlips(DrawingHandleBase handle, List<BlipData> blipDataList)
        {
            var blipValueList = new Dictionary<Color, ValueList<Vector2>>();

            foreach (var blipData in blipDataList)
            {
                var triangleShapeVectorPoints = new[]
                {
                new Vector2(0, 0),
                new Vector2(RadarBlipSize, 0),
                new Vector2(RadarBlipSize * 0.5f, RadarBlipSize)
            };

                if (blipData.IsOutsideRadarCircle)
                {
                    // Calculate the angle of rotation
                    var angle = (float) Math.Atan2(blipData.VectorToPosition.Y, blipData.VectorToPosition.X) + -1.6f;

                    // Manually create a rotation matrix
                    var cos = (float) Math.Cos(angle);
                    var sin = (float) Math.Sin(angle);
                    float[,] rotationMatrix = { { cos, -sin }, { sin, cos } };

                    // Rotate each vertex
                    for (var i = 0; i < triangleShapeVectorPoints.Length; i++)
                    {
                        var vertex = triangleShapeVectorPoints[i];
                        var x = vertex.X * rotationMatrix[0, 0] + vertex.Y * rotationMatrix[0, 1];
                        var y = vertex.X * rotationMatrix[1, 0] + vertex.Y * rotationMatrix[1, 1];
                        triangleShapeVectorPoints[i] = new Vector2(x, y);
                    }
                }

                var triangleCenterVector =
                    (triangleShapeVectorPoints[0] + triangleShapeVectorPoints[1] + triangleShapeVectorPoints[2]) / 3;

                // Calculate the vectors from the center to each vertex
                var vectorsFromCenter = new Vector2[3];
                for (int i = 0; i < 3; i++)
                {
                    vectorsFromCenter[i] = (triangleShapeVectorPoints[i] - triangleCenterVector) * UIScale;
                }

                // Calculate the vertices of the new triangle
                var newVerts = new Vector2[3];
                for (var i = 0; i < 3; i++)
                {
                    newVerts[i] = (blipData.UiPosition * UIScale) + vectorsFromCenter[i];
                }

                if (!blipValueList.TryGetValue(blipData.Color, out var valueList))
                {
                    valueList = new ValueList<Vector2>();

                }
                valueList.Add(newVerts[0]);
                valueList.Add(newVerts[1]);
                valueList.Add(newVerts[2]);
                blipValueList[blipData.Color] = valueList;
            }

            // One draw call for every color we have
            foreach (var color in blipValueList)
            {
                handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, color.Value.Span, color.Key);
            }
        }
    }
}
