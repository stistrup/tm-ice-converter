import { Color, DataTexture, DoubleSide, ShaderMaterial, Texture, Vector4 } from 'three'

// one shader for every scene material: textures by UV or projected (top, sides), terrain, block colours, glow, the cut pass
const vertexShader = /* glsl */ `
  uniform float flipV;
  attribute vec4 cv;
  varying vec4 vCv;
  varying vec3 vWorld;
  varying vec3 vNormal;
  varying vec2 vUv;
  varying float vTint;

  void main() {
    mat4 world = modelMatrix;
    #ifdef USE_INSTANCING_COLOR
      vTint = instanceColor.r * 8.0;
    #else
      vTint = 0.0;
    #endif
    #ifdef USE_INSTANCING
      world = modelMatrix * instanceMatrix;
    #endif
    vec4 position4 = world * vec4(position, 1.0);
    vWorld = position4.xyz;
    vNormal = mat3(world) * normal;
    vUv = flipV > 0.5 ? vec2(uv.x, 1.0 - uv.y) : uv;
    vCv = cv;
    gl_Position = projectionMatrix * viewMatrix * position4;
  }
`

const fragmentShader = /* glsl */ `
  uniform sampler2D mapUv;
  uniform sampler2D mapTop;
  uniform sampler2D mapSide;
  uniform vec3 hasMap;
  uniform vec3 baseColor;
  uniform sampler2D mapHue;
  uniform float hasHue;
  uniform sampler2D mapIllum;
  uniform float hasIllum;
  uniform float additive;
  uniform float solidGlow;
  uniform vec3 tints[5];
  uniform vec3 sideColor;
  uniform float terrain;
  uniform float cutout;
  uniform float decal;
  uniform float ice;
  uniform float opacity;
  uniform float water;
  uniform float greyGreen;
  // the ground blocks' road surface (MapScene's cut pass): terrain above it inside their footprint is cut away
  uniform sampler2D cutMap;
  uniform vec4 cutBox;
  uniform float hasCut;

  varying vec3 vWorld;
  varying vec3 vNormal;
  varying vec2 vUv;
  varying float vTint;
  varying vec4 vCv;

  const float TILE = 1.0 / 32.0;

  void main() {
    vec3 n = dot(vNormal, vNormal) > 0.0001
      ? normalize(vNormal)
      : normalize(cross(dFdx(vWorld), dFdy(vWorld)));
    if (!gl_FrontFacing) n = -n;
    // only the surface: the volume's sides would show through
    if (water > 0.5 && abs(n.y) < 0.7) discard;
    if (terrain > 0.5 && hasCut > 0.5) {
      vec2 cuv = (vWorld.xz - cutBox.xy) / cutBox.zw;
      if (cuv.x >= 0.0 && cuv.x <= 1.0 && cuv.y >= 0.0 && cuv.y <= 1.0) {
        // blended between texels: height over cover is the covered texels' own height
        vec2 cut = texture2D(cutMap, cuv).rg;
        // level with the road counts too: inside a block the road is what shows
        if (cut.g > 0.5 && vWorld.y > cut.r / cut.g - 0.3) discard;
      }
    }

    vec4 albedo = vec4(baseColor, 1.0);
    if (terrain > 0.5) albedo.rgb = mix(sideColor, baseColor, smoothstep(0.55, 0.8, n.y));
    if (hasMap.x > 0.5) albedo = texture2D(mapUv, vUv);
    if (hasMap.z > 0.5) {
      vec2 sideUv = abs(n.x) > abs(n.z) ? vWorld.zy : vWorld.xy;
      // no top texture: flat faces take the side one from above
      if (hasMap.y < 0.5 && abs(n.y) > max(abs(n.x), abs(n.z))) sideUv = vec2(vWorld.x, -vWorld.z);
      albedo = texture2D(mapSide, sideUv * TILE);
    }
    if (hasMap.y > 0.5) {
      float up = smoothstep(0.55, 0.8, n.y);
      vec4 top = texture2D(mapTop, vec2(vWorld.x, -vWorld.z) * TILE);
      albedo = hasMap.x + hasMap.z > 0.5 ? mix(albedo, top, up) : top;
    }

    // _Hue shaders (Custom* library materials): the texture is brightness in its green channel, the hue (white here) colours it
    if (greyGreen > 0.5) albedo.rgb = vec3(albedo.g);

    // the block's colour repaints the hue mask's area: alpha = how much, green = brightness; Default keeps the painted green
    int tint = int(vTint + 0.5);
    if (hasHue > 0.5 && tint >= 1 && tint <= 5) {
      vec4 hue = texture2D(mapHue, vUv);
      albedo.rgb = mix(albedo.rgb, tints[tint - 1] * hue.g, hue.a);
    }

    if (ice > 0.5) {
      // rink markings are faint: boost them
      // the numbers are for raw texture values
      vec3 raw = pow(albedo.rgb, vec3(1.0 / 2.2));
      vec3 boosted = clamp(raw * 2.0 + vec3(0.42, 0.62, 0.88) - vec3(0.70, 0.74, 0.78) * 2.0, 0.0, 1.0);
      albedo = vec4(hasMap.x > 0.5 ? boosted : vec3(0.42, 0.62, 0.88), 1.0);
    }

    float alpha = decal > 0.5 ? albedo.a : 1.0;
    if (cutout > 0.5 && albedo.a < 0.5) discard;
    if (alpha < 0.01) discard;

    vec3 sun = normalize(vec3(0.45, 0.8, 0.35));
    vec3 light = mix(vec3(0.33, 0.33, 0.36), vec3(0.72, 0.8, 0.9), n.y * 0.5 + 0.5)
      + vec3(0.62, 0.58, 0.5) * max(dot(n, sun), 0.0);
    // self-lit parts (sign arrows, timers, screens) glow on top; additive FX (waypoint triggers) are only that glow
    vec3 illum = hasIllum < 0.5 ? vec3(0.0)
      // the smallest mip = the texture's average: its background colour without the detail
      : solidGlow > 0.5 ? textureLod(mapIllum, vUv, 16.0).rgb
      : texture2D(mapIllum, vUv).rgb;
    // additive FX (TAddModCV, waypoint triggers): glow x vertex colour; the colours are 8-bit sRGB like textures
    vec3 lit = additive > 0.5 ? illum * pow(vCv.rgb, vec3(2.2)) : albedo.rgb * light + illum;
    gl_FragColor = vec4(lit, alpha * opacity);
    #include <colorspace_fragment>
  }
`

const blankTexture = (): Texture => {
  const texture = new DataTexture(new Uint8Array([255, 255, 255, 255]), 1, 1)
  texture.needsUpdate = true
  return texture
}

export const blank = blankTexture()

export const flatMaterial = (color: [number, number, number]): ShaderMaterial => {
  return new ShaderMaterial({
    vertexShader,
    fragmentShader,
    side: DoubleSide,
    uniforms: {
      mapUv: { value: blank },
      mapTop: { value: blank },
      mapSide: { value: blank },
      hasMap: { value: new Color(0, 0, 0) },
      baseColor: { value: new Color(...color) },
      sideColor: { value: new Color(...color) },
      terrain: { value: 0 },
      cutout: { value: 0 },
      decal: { value: 0 },
      ice: { value: 0 },
      opacity: { value: 1 },
      flipV: { value: 0 },
      mapHue: { value: blank },
      hasHue: { value: 0 },
      mapIllum: { value: blank },
      hasIllum: { value: 0 },
      additive: { value: 0 },
      solidGlow: { value: 0 },
      tints: { value: Array.from({ length: 5 }, () => new Color(1, 1, 1)) },
      water: { value: 0 },
      greyGreen: { value: 0 },
      cutMap: { value: blank },
      cutBox: { value: new Vector4(0, 0, 1, 1) },
      hasCut: { value: 0 },
    },
  })
}
