import { DoubleSide, ShaderMaterial } from 'three'

// a piece still being worked on: gray, a white spinner on each block cell's top, the rest pulsing
export const pendingMaterial = () =>
  new ShaderMaterial({
    uniforms: { time: { value: 0 } },
    vertexShader: /* glsl */ `
      varying vec3 vWorld;
      varying vec3 vNormal;
      void main() {
        mat4 world = modelMatrix;
        #ifdef USE_INSTANCING
          world = modelMatrix * instanceMatrix;
        #endif
        vec4 position4 = world * vec4(position, 1.0);
        vWorld = position4.xyz;
        vNormal = mat3(world) * normal;
        gl_Position = projectionMatrix * viewMatrix * position4;
      }
    `,
    fragmentShader: /* glsl */ `
      uniform float time;
      varying vec3 vWorld;
      varying vec3 vNormal;
      // one spinner per block cell, on the tops
      const float CELL = 32.0;
      const float TAU = 6.2831853;
      void main() {
        vec3 n = dot(vNormal, vNormal) > 0.0001 ? normalize(vNormal) : normalize(cross(dFdx(vWorld), dFdy(vWorld)));
        vec3 a = abs(n);
        float light = 0.75 + 0.25 * a.y;
        float pulse = 0.04 * sin(time * 3.0);
        float spinner = 0.0;
        if (a.y > max(a.x, a.z)) {
          vec2 p = fract(vWorld.xz / CELL) - 0.5;
          float r = length(p);
          float ring = smoothstep(0.17, 0.18, r) * (1.0 - smoothstep(0.28, 0.29, r));
          // three quarters of the ring, brightest at its head, turning
          float along = fract(atan(p.y, p.x) / TAU + time * 0.8);
          spinner = ring * smoothstep(0.0, 0.75, along) * step(along, 0.75);
        }
        vec3 colour = mix(vec3(0.32 + pulse), vec3(0.95), spinner) * light;
        gl_FragColor = vec4(colour, 1.0);
        #include <colorspace_fragment>
      }
    `,
    side: DoubleSide,
  })
