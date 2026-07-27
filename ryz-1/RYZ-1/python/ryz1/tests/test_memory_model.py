from ryz1.models.policy_value import ModelConfig


def test_model_memory_reset_and_persistence_shapes():
    import torch
    from ryz1.models.policy_value import RyzPolicyValueModel

    cfg = ModelConfig(player_vector_size=16, mechanics_vector_size=32, macro_count=9, hidden_size=32)
    model = RyzPolicyValueModel(cfg)
    player = torch.zeros(2, 3, 16)
    mechanics = torch.zeros(2, 3, 32)
    history = torch.zeros(2, 3, 4)
    memory = model.initial_memory(2)
    out = model(player, mechanics, history, memory)
    assert out["memory"].shape == memory.shape
    reset = model.initial_memory(2)
    assert torch.all(reset == 0)
