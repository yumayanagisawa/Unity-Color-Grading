
Shader "ALIyerEdon/Mobile/PlanarReflection__Glass"
{
	Properties
	{
		_Color("Color",Color) = (1,1,1,1)
		_MainAlpha("MainAlpha", Range(0, 1)) = 1
		_ReflectionAlpha("ReflectionAlpha", Range(0, 1)) = 1
		_MainTex ("MainTex (RGBA)", 2D) = ""
		[HideInInspector]_ReflectionTex ("ReflectionTex", 2D) = "white" { TexGen ObjectLinear }
	}


	Subshader
	{
	Tags {"Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent"}
	LOD 200

		CGPROGRAM
		#pragma surface surf Lambert alpha:fade

		float _MainAlpha;
		float _ReflectionAlpha;
		sampler2D _MainTex;
		sampler2D _ReflectionTex;
		half4 _Color;

		struct Input
		{
			float2 uv_MainTex;
			float4 screenPos;
		};

		void surf(Input IN, inout SurfaceOutput o)
		{

			fixed4 main_Tex = tex2D(_MainTex, IN.uv_MainTex);

			o.Albedo =  main_Tex.rgb  * _MainAlpha * _Color.rgb;

			o.Emission = tex2D(_ReflectionTex, IN.screenPos.xy / IN.screenPos.w).rgb * _ReflectionAlpha * main_Tex.a;

			o.Alpha = _Color.a;
		}
		ENDCG
		}

	Fallback "Diffuse"

}