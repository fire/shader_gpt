using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Linq;

namespace ShaderGPT {
public class GPTNeo : GPTBase {
	[System.Serializable]
	class Config {
		public int num_layers;
		public int num_heads;
		public float layer_norm_epsilon;
		public string activation_function;
		public int max_position_embeddings;
		public int vocab_size;
		public int window_size;
	}
	Config config;

	public new void OnEnable() {
		config = JsonUtility.FromJson<Config>(configJson.text);
		maxLength = Mathf.Min(maxLength, config.max_position_embeddings);
		base.OnEnable();
	}
	public override int Run(int positionId) {
		var input = InputTensor(tokens, positionId);
		var (hidden_states, logits) = GPTNeoForCausalLM(input, position_id:positionId);
		ctx.Release(input);
		ctx.Release(hidden_states);
		var next_tokens = MultinomialSample(logits, config.vocab_size, temperature);
		ctx.Release(logits);
		var data = BatchRelease(ctx.GetData((RenderTexture)MarkRelease(next_tokens)));
		return Mathf.RoundToInt(data[0]);
	}
	public override void Test(Testcase testcase) {
		var input = InputTensor(testcase.input_ids);
		var (hidden_states, logits) = GPTNeoForCausalLM(input);
		ctx.Release(input);
		AssertData((RenderTexture)hidden_states, -1, testcase.hidden_states, 3e-4f);
		AssertData((RenderTexture)logits, -1, testcase.logits, 2e-3f);
		ctx.Release(hidden_states);
		ctx.Release(logits);
	}
	public override void Bake() {
		var input = ctx.PersistentGPUTensor("input", 1, 1);
		var (hidden_states, logits) = GPTNeoForCausalLM(input);
		ctx.Release(hidden_states);
		var next_tokens = MultinomialSample(logits, config.vocab_size);
		ctx.Release(logits);
		nn.Copy(input, next_tokens, ctx.Size(input));
		ctx.Release(next_tokens);
	}

	void GPTNeoSelfAttention(ref Texture hidden_states, string path, int position_id, int layer_id) {
		var query = nn.Linear(hidden_states, parameters[$"{path}.q_proj.weight"]);
		var key   = nn.Linear(hidden_states, parameters[$"{path}.k_proj.weight"]);
		var value = nn.Linear(hidden_states, parameters[$"{path}.v_proj.weight"]);
		ctx.Release(hidden_states);

		var keys   = ctx.PersistentGPUTensor($"{path}.k", maxLength, ctx.Size1(key), dtype:nn.dataType);
		var values = ctx.PersistentGPUTensor($"{path}.v", maxLength, ctx.Size1(value), dtype:nn.dataType);
		BatchRelease(nn.Copy(keys,   MarkRelease(key),   outputOffset:new Vector2Int(position_id, 0), size:ctx.Size(key)));
		BatchRelease(nn.Copy(values, MarkRelease(value), outputOffset:new Vector2Int(position_id, 0), size:ctx.Size(value)));

		var window_size = layer_id%2==1 ? config.window_size : config.max_position_embeddings;
		var causal_mask = new Vector4(1, 1, position_id+1-window_size, position_id+1);
		var attn_scores = BatchRelease(nn.Linear(MarkRelease(query), keys, head:config.num_heads));
		var attn_weights = BatchRelease(nn.Softmax(MarkRelease(attn_scores), group:config.num_heads, rangeMask:causal_mask));
		hidden_states = BatchRelease(nn.Linear(MarkRelease(attn_weights), values, head:config.num_heads, transposeWeight:true));
		hidden_states = BatchRelease(nn.Linear(MarkRelease(hidden_states), parameters[$"{path}.out_proj.weight"]));
		hidden_states = BatchRelease(nn.AddAct(MarkRelease(hidden_states), parameters[$"{path}.out_proj.bias"]));
	}
	void GPTNeoBlock(ref Texture hidden_states, string path, int position_id, int layer_id) {
		var residual = hidden_states;
		hidden_states = nn.GroupNorm(residual, parameters[$"{path}.ln_1.weight"], parameters[$"{path}.ln_1.bias"], config.layer_norm_epsilon);
		GPTNeoSelfAttention(ref hidden_states, path:$"{path}.attn.attention", position_id:position_id, layer_id:layer_id);
		hidden_states = BatchRelease(nn.AddAct(MarkRelease(hidden_states), MarkRelease(residual)));

		residual = hidden_states;
		hidden_states = nn.GroupNorm(residual, parameters[$"{path}.ln_2.weight"], parameters[$"{path}.ln_2.bias"], config.layer_norm_epsilon);
		hidden_states = BatchRelease(nn.Linear(MarkRelease(hidden_states), parameters[$"{path}.mlp.c_fc.weight"]));
		hidden_states = BatchRelease(nn.AddAct(MarkRelease(hidden_states), parameters[$"{path}.mlp.c_fc.bias"], TensorNN.ActFn(config.activation_function)));
		hidden_states = BatchRelease(nn.Linear(MarkRelease(hidden_states), parameters[$"{path}.mlp.c_proj.weight"]));
		hidden_states = BatchRelease(nn.AddAct(MarkRelease(hidden_states), parameters[$"{path}.mlp.c_proj.bias"]));
		hidden_states = BatchRelease(nn.AddAct(MarkRelease(hidden_states), MarkRelease(residual)));
	}
	Texture GPTNeoModel(Texture input_ids, string path, int position_id) {
		var hidden_states = nn.Embedding(input_ids, parameters[$"{path}.wte.weight.T"], parameters[$"{path}.wpe.weight.T"], transposeWeight:true);
		for(int i=0; i<config.num_layers; i++)
			GPTNeoBlock(ref hidden_states, path:$"{path}.h.{i}", position_id:position_id, layer_id:i);
		hidden_states = BatchRelease(nn.GroupNorm(MarkRelease(hidden_states), parameters[$"{path}.ln_f.weight"], parameters[$"{path}.ln_f.bias"], config.layer_norm_epsilon));
		return hidden_states;
	}
	(Texture, Texture) GPTNeoForCausalLM(Texture input_ids, int position_id=0) {
		var hidden_states = GPTNeoModel(input_ids, path:"transformer", position_id:position_id);
		var lm_logits = nn.Linear(hidden_states, parameters["transformer.wte.weight.T"], transposeWeight:true);
		return (hidden_states, lm_logits);
	}
}
}